using Mono.Cecil;
using Mono.Cecil.Cil;
namespace Aave.WaitOwnership.CallerSource;

public sealed record CompletionSitePlan(int SiteId,int IlOffset,CompletionFactKind Kind,int TargetPlanId,string ExactMember);
public sealed record CompletionReturnField(int SiteId,int FieldToken,string ExactField);
public sealed record CompletionMethodPlan(int PlanId,Guid ModuleMvid,int MethodToken,string OriginalFingerprint,bool InstanceMethod,
    IReadOnlyList<CompletionSitePlan> Sites,CallerMethodPlan? CallerPlan=null,IReadOnlyList<CompletionReturnField>? ReturnFields=null);
public sealed record AddedOperand(string OpCode,int MetadataToken,string Member,string Scope);
public sealed record CompletionRewriteReceipt(CompletionMethodPlan Plan,int OriginalInstructions,int FinalInstructions,string[] HelperCalls,AddedOperand[] AddedOperands);

// Actual opcode source adapter. It preserves each selected business opcode and
// feeds its real operands/results into the helper, rather than reevaluating it.
public static class CompletionFactsRewriter
{
    public static CompletionRewriteReceipt Apply(MethodDefinition method,CompletionMethodPlan plan)
    {
        if(!method.HasBody||method.HasGenericParameters||method.Module.Mvid!=plan.ModuleMvid||method.MetadataToken.ToInt32()!=plan.MethodToken||CallerSourceRewriter.Fingerprint(method)!=plan.OriginalFingerprint||plan.PlanId<=0)
            throw new InvalidDataException("Exact completion source input differs");
        var body=method.Body;var raw=body.Instructions.ToArray();
        if(!body.InitLocals&&body.Variables.Count!=0)throw new NotSupportedException("Original uninitialized locals unsupported");
        if(raw.Any(i=>i.OpCode.Code is Code.Jmp or Code.Tail))throw new NotSupportedException("Unsupported source transfer");
        if(plan.Sites.Select(x=>x.SiteId).Distinct().Count()!=plan.Sites.Count||plan.Sites.Select(x=>x.IlOffset).Distinct().Count()!=plan.Sites.Count)throw new InvalidDataException("Duplicate source site");
        var selected=plan.Sites.Select(site=>{
            var node=raw.SingleOrDefault(i=>i.Offset==site.IlOffset)??throw new InvalidDataException("Source opcode absent");
            if(node.Operand is not MemberReference member||member.FullName!=site.ExactMember||node.Previous?.OpCode.OpCodeType==OpCodeType.Prefix)throw new InvalidDataException("Exact member/prefix differs");
            if(node.OpCode.Code is not(Code.Call or Code.Callvirt or Code.Newobj or Code.Ldfld or Code.Stfld or Code.Ldsfld or Code.Stsfld))throw new NotSupportedException("Unsupported fact opcode");
            return(site,node);
        }).ToArray();
        var returns=(plan.ReturnFields??Array.Empty<CompletionReturnField>()).Select(p=>{
            var field=raw.Select(i=>i.Operand).OfType<FieldReference>().Where(f=>f.FullName==p.ExactField).Select(f=>f.Resolve()).FirstOrDefault(f=>f.MetadataToken.ToInt32()==p.FieldToken)
                ??method.Module.LookupToken(p.FieldToken) as FieldDefinition??throw new InvalidDataException("Return field token absent");
            string owner=method.HasThis?method.DeclaringType.FullName:plan.InstanceMethod&&method.Parameters.Count>0?method.Parameters[0].ParameterType.FullName:method.DeclaringType.FullName;
            if(field.FullName!=p.ExactField||field.DeclaringType.FullName!=owner||field.IsStatic)throw new InvalidDataException("Return snapshot must use exact own instance field");return(p,field);
        }).ToArray();
        if(plan.CallerPlan!=null)CallerSourceRewriter.Apply(method,plan.CallerPlan);
        var il=body.GetILProcessor();var helpers=new List<Instruction>();var businessStart=raw[0];
        MethodReference Hook(string name)=>method.Module.ImportReference(typeof(CompletionFactsHooks).GetMethod(name)!);
        MethodReference CallerHook(string name)=>method.Module.ImportReference(typeof(CallerSourceHooks).GetMethod(name)!);
        Instruction L(VariableDefinition v)=>Instruction.Create(OpCodes.Ldloc,v);
        Instruction S(VariableDefinition v)=>Instruction.Create(OpCodes.Stloc,v);
        VariableDefinition Local(TypeReference t){var v=new VariableDefinition(method.Module.ImportReference(t));body.Variables.Add(v);return v;}
        void Before(Instruction target,IEnumerable<Instruction> nodes,bool redirect=false)
        {
            var added=nodes.ToArray();if(added.Length==0)return;
            if(redirect)
            {
                if(target==businessStart)businessStart=added[0];
                // An incoming branch already carries exactly the business opcode's
                // operands. It must enter the spill block, not skip observation.
                // Move every EH boundary too so the spill has the opcode's region.
                foreach(var old in body.Instructions)
                {if(ReferenceEquals(old.Operand,target))old.Operand=added[0];else if(old.Operand is Instruction[] targets)for(int n=0;n<targets.Length;n++)if(targets[n]==target)targets[n]=added[0];}
                foreach(var h in body.ExceptionHandlers)
                {if(h.TryStart==target)h.TryStart=added[0];if(h.TryEnd==target)h.TryEnd=added[0];if(h.HandlerStart==target)h.HandlerStart=added[0];if(h.HandlerEnd==target)h.HandlerEnd=added[0];if(h.FilterStart==target)h.FilterStart=added[0];}
            }
            foreach(var n in added){il.InsertBefore(target,n);helpers.Add(n);}
        }
        void After(Instruction target,IEnumerable<Instruction> nodes){foreach(var n in nodes){il.InsertAfter(target,n);helpers.Add(n);target=n;}}
        VariableDefinition frame;
        if(plan.CallerPlan==null)
        {
            frame=Local(method.Module.ImportReference(typeof(CallerSourceHooks.SourceFrame)));
            var entry=new List<Instruction>{Instruction.Create(OpCodes.Ldc_I4,plan.PlanId),FactShape.Receiver(method,plan.InstanceMethod)};
            entry.AddRange(FactShape.Arguments(method,plan.InstanceMethod));entry.Add(Instruction.Create(OpCodes.Call,CallerHook(nameof(CallerSourceHooks.EnterGeneral))));entry.Add(S(frame));Before(raw[0],entry);
        }
        else frame=body.Variables.Single(v=>v.VariableType.FullName==typeof(CallerSourceHooks.SourceFrame).FullName!.Replace('+','/'));
        var callTokens=new List<VariableDefinition>();
        foreach(var (site,node) in selected)
        {
            if(node.Operand is MethodReference called)
            {
                if(called.HasGenericParameters||called.Parameters.Any(p=>p.ParameterType.IsByReference||p.ParameterType.IsPointer))throw new NotSupportedException("Generic/byref call operands unsupported");
                bool construction=node.OpCode.Code==Code.Newobj;bool hasReceiver=called.HasThis&&!construction;
                var args=called.Parameters.Select(p=>Local(Substitute(p.ParameterType,called))).ToArray();
                VariableDefinition? receiver=hasReceiver?Local(called.DeclaringType):null;
                var values=Local(method.Module.ImportReference(typeof(FactArguments)));var token=Local(method.Module.ImportReference(typeof(CompletionCallToken)));callTokens.Add(token);
                var before=new List<Instruction>();for(int i=args.Length-1;i>=0;i--)before.Add(S(args[i]));if(receiver!=null)before.Add(S(receiver));
                before.Add(Instruction.Create(OpCodes.Ldc_I4,args.Length));before.Add(Instruction.Create(OpCodes.Call,Hook(nameof(CompletionFactsHooks.NewArguments))));before.Add(S(values));
                for(int i=0;i<args.Length;i++){before.Add(L(values));before.Add(Instruction.Create(OpCodes.Ldc_I4,i));before.Add(L(args[i]));before.AddRange(FactShape.Box(method.Module,args[i].VariableType));before.Add(Instruction.Create(OpCodes.Call,Hook(nameof(CompletionFactsHooks.SetArgument))));}
                before.AddRange(new[]{L(frame),Instruction.Create(OpCodes.Ldc_I4,site.SiteId),Instruction.Create(OpCodes.Ldc_I4,(int)site.Kind),Instruction.Create(OpCodes.Ldc_I4,site.TargetPlanId),receiver==null?Instruction.Create(OpCodes.Ldnull):L(receiver),L(values),Instruction.Create(OpCodes.Call,Hook(nameof(CompletionFactsHooks.BeforeCall))),S(token)});
                if(receiver!=null)before.Add(L(receiver));before.AddRange(args.Select(L));Before(node,before,true);
                TypeReference resultType=construction?called.DeclaringType:Substitute(called.ReturnType,called);
                VariableDefinition? result=resultType.MetadataType==MetadataType.Void?null:Local(resultType);
                var after=new List<Instruction>();if(result!=null)after.Add(S(result));after.Add(L(token));
                if(result==null)after.Add(Instruction.Create(OpCodes.Ldnull));else{after.Add(L(result));after.AddRange(FactShape.Box(method.Module,resultType));}
                after.Add(Instruction.Create(OpCodes.Call,Hook(nameof(CompletionFactsHooks.AfterCall))));if(result!=null)after.Add(L(result));After(node,after);
            }
            else if(node.Operand is FieldReference field)
            {
                bool write=node.OpCode.Code is Code.Stfld or Code.Stsfld,instance=node.OpCode.Code is Code.Stfld or Code.Ldfld;
                var value=Local(field.FieldType);var receiver=instance?Local(field.DeclaringType):null;var before=new List<Instruction>();
                if(write)before.Add(S(value));if(receiver!=null)before.Add(S(receiver));if(receiver!=null)before.Add(L(receiver));if(write)before.Add(L(value));Before(node,before,true);
                var after=new List<Instruction>();if(!write)after.Add(S(value));after.Add(L(frame));after.Add(Instruction.Create(OpCodes.Ldc_I4,site.SiteId));after.Add(receiver==null?Instruction.Create(OpCodes.Ldnull):L(receiver));after.Add(L(value));after.AddRange(FactShape.Box(method.Module,field.FieldType));after.Add(Instruction.Create(OpCodes.Call,Hook(write?nameof(CompletionFactsHooks.FieldWritten):nameof(CompletionFactsHooks.FieldRead))));if(!write)after.Add(L(value));After(node,after);
            }
        }
        // A selected call may throw into an existing catch/filter/finally. These
        // source-local tokens are revoked on entry without touching the exception
        // already on the evaluation stack. No exception is caught or replaced.
        var handlers=body.ExceptionHandlers.SelectMany(h=>new[]{h.HandlerStart,h.FilterStart}).Where(i=>i!=null).Distinct().Cast<Instruction>().ToArray();
        foreach(var start in handlers)
        {
            var cleanup=callTokens.SelectMany(v=>new[]{L(v),Instruction.Create(OpCodes.Call,Hook(nameof(CompletionFactsHooks.AbandonCall)))}).ToArray();
            if(cleanup.Length==0)continue;Before(start,cleanup);
            foreach(var h in body.ExceptionHandlers){if(h.HandlerStart==start)h.HandlerStart=cleanup[0];if(h.FilterStart==start)h.FilterStart=cleanup[0];if(h.TryEnd==start)h.TryEnd=cleanup[0];if(h.HandlerEnd==start)h.HandlerEnd=cleanup[0];}
        }
        if(plan.CallerPlan==null)
        {
            var result=method.ReturnType.MetadataType==MetadataType.Void?null:Local(method.ReturnType);
            var success=Instruction.Create(OpCodes.Nop);var cleanup=L(frame);var done=result==null?Instruction.Create(OpCodes.Ret):L(result);
            foreach(var h in body.ExceptionHandlers){h.TryEnd??=success;h.HandlerEnd??=success;}
            foreach(var ret in raw.Where(i=>i.OpCode.Code==Code.Ret))
            {if(result==null){ret.OpCode=OpCodes.Leave;ret.Operand=success;}else{ret.OpCode=OpCodes.Stloc;ret.Operand=result;After(ret,new[]{Instruction.Create(OpCodes.Leave,success)});}}
            il.Append(success);
            foreach(var (p,field) in returns)
            {
                var fieldValue=Local(field.FieldType);il.Append(FactShape.Receiver(method,plan.InstanceMethod));var read=Instruction.Create(OpCodes.Ldfld,field);il.Append(read);helpers.Add(read);il.Append(S(fieldValue));il.Append(L(frame));il.Append(Instruction.Create(OpCodes.Ldc_I4,p.SiteId));il.Append(FactShape.Receiver(method,plan.InstanceMethod));il.Append(L(fieldValue));foreach(var b in FactShape.Box(method.Module,field.FieldType)){il.Append(b);helpers.Add(b);}var observe=Instruction.Create(OpCodes.Call,Hook(nameof(CompletionFactsHooks.FieldRead)));il.Append(observe);helpers.Add(observe);
            }
            il.Append(L(frame));if(result==null)il.Append(Instruction.Create(OpCodes.Ldnull));else{il.Append(L(result));foreach(var b in FactShape.Box(method.Module,method.ReturnType)){il.Append(b);helpers.Add(b);}}
            var returned=Instruction.Create(OpCodes.Call,CallerHook(nameof(CallerSourceHooks.ReturnedValue)));il.Append(returned);helpers.Add(returned);il.Append(Instruction.Create(OpCodes.Leave,done));il.Append(cleanup);var exit=Instruction.Create(OpCodes.Call,CallerHook(nameof(CallerSourceHooks.Exit)));il.Append(exit);helpers.Add(exit);il.Append(Instruction.Create(OpCodes.Endfinally));il.Append(done);if(result!=null)il.Append(Instruction.Create(OpCodes.Ret));
            body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally){TryStart=businessStart,TryEnd=cleanup,HandlerStart=cleanup,HandlerEnd=done});
        }
        foreach(var n in body.Instructions)n.OpCode=Long(n.OpCode);
        body.InitLocals=true;body.MaxStackSize=checked(body.MaxStackSize+12);
        var originalNodes=new HashSet<Instruction>(raw);
        var added=body.Instructions.Where(i=>!originalNodes.Contains(i)&&i.Operand is MemberReference).Select(i=>{var m=(MemberReference)i.Operand;return new AddedOperand(i.OpCode.Name,m.MetadataToken.ToInt32(),m.FullName,m.DeclaringType?.Scope?.ToString()??"");}).ToArray();
        return new(plan,raw.Length,body.Instructions.Count,body.Instructions.Where(i=>i.Operand is MethodReference m&&(m.DeclaringType.FullName==typeof(CallerSourceHooks).FullName||m.DeclaringType.FullName==typeof(CompletionFactsHooks).FullName)).Select(i=>((MethodReference)i.Operand).Name).ToArray(),added);
    }
    static TypeReference Substitute(TypeReference type,MethodReference method)
    {
        if(type is GenericParameter p){if(p.Type==GenericParameterType.Type&&method.DeclaringType is GenericInstanceType t)return t.GenericArguments[p.Position];if(p.Type==GenericParameterType.Method&&method is GenericInstanceMethod m)return m.GenericArguments[p.Position];throw new NotSupportedException("Open generic operand");}return type;
    }
    static OpCode Long(OpCode op)=>op.Code switch{Code.Br_S=>OpCodes.Br,Code.Brtrue_S=>OpCodes.Brtrue,Code.Brfalse_S=>OpCodes.Brfalse,Code.Beq_S=>OpCodes.Beq,Code.Bne_Un_S=>OpCodes.Bne_Un,Code.Bge_S=>OpCodes.Bge,Code.Bge_Un_S=>OpCodes.Bge_Un,Code.Bgt_S=>OpCodes.Bgt,Code.Bgt_Un_S=>OpCodes.Bgt_Un,Code.Ble_S=>OpCodes.Ble,Code.Ble_Un_S=>OpCodes.Ble_Un,Code.Blt_S=>OpCodes.Blt,Code.Blt_Un_S=>OpCodes.Blt_Un,Code.Leave_S=>OpCodes.Leave,_=>op};
}

