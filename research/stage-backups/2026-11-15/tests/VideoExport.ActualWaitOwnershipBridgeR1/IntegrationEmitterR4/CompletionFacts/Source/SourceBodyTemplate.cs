using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Utils;
using ManagedOriginalCallWitness;
namespace Aave.WaitOwnership.CallerSource;

// Exact original-source body input, captured before foreign Harmony transforms.
// It is compared as a complete contiguous body, not by names or reason strings.
internal sealed class SourceBodyTemplate
{
    private readonly string[] forms;
    private readonly MemberInfo?[] members;
    private readonly Type[] locals;
    private readonly Clause[] clauses;
    private readonly int enterIndex;
    private readonly Dictionary<(int Offset,int Plan),MethodBase> releaseSites=new();
    private readonly Dictionary<(int Site,int Plan),(MethodBase Method,bool ReceiverArgument,Type? ValueType)> factSites=new();
    private readonly Dictionary<int,(FieldInfo Field,Type ValueType)> factFields=new();
    internal bool IsGeneral=>kind==CallerSourceKind.Observation;
    private readonly int planId;
    private readonly CallerSourceKind kind;
    private sealed record Clause(ExceptionHandlerType Kind,int Start,int End,int Handler,int HandlerEnd,int Filter,Type? Catch);
    private static readonly MethodInfo Enter=typeof(CallerSourceHooks).GetMethod(nameof(CallerSourceHooks.Enter))!;
    private SourceBodyTemplate(Mono.Cecil.Cil.MethodBody body,int id,CallerSourceKind k)
    {
        planId=id;kind=k;var nodes=body.Instructions.ToArray();var flow=new ControlFlowOperands(body);
        int Index(Instruction? i)=>flow.Position(i,true);
        forms=nodes.Select(i=>Form(i,Index,flow)).ToArray();
        members=nodes.Select(i=>i.Operand is MemberReference m?ExactRuntimeIdentity.Resolve(m):null).ToArray();
        locals=body.Variables.Select(v=>ExactRuntimeIdentity.ResolveType(v.VariableType)).ToArray();
        clauses=body.ExceptionHandlers.Select(h=>new Clause(h.HandlerType,Index(h.TryStart),Index(h.TryEnd),Index(h.HandlerStart),Index(h.HandlerEnd),h.FilterStart==null?-1:Index(h.FilterStart),h.CatchType==null?null:ExactRuntimeIdentity.ResolveType(h.CatchType))).ToArray();
        var entryMethod=IsGeneral?typeof(CallerSourceHooks).GetMethod(nameof(CallerSourceHooks.EnterGeneral))!:Enter;
        var enters=Enumerable.Range(0,members.Length).Where(i=>members[i]!=null&&ExactRuntimeIdentity.Same(members[i]!,entryMethod)).ToArray();
        if(enters.Length!=1||(!IsGeneral&&(enters[0]!=5||forms[1]!="ldc.i4:"+(int)k))||forms[0]!="ldc.i4:"+id||forms[^1]!="ret:")
            throw new InvalidDataException("Exact source entry/plan/full body template absent");
        enterIndex=enters[0];
        var beforeRelease=typeof(CallerSourceHooks).GetMethod(nameof(CallerSourceHooks.BeforeReleaseCall))!;
        var afterRelease=typeof(CallerSourceHooks).GetMethod(nameof(CallerSourceHooks.AfterReleaseCall))!;
        for(int i=0;i<nodes.Length;i++)
        {
            if(members[i]==null||!ExactRuntimeIdentity.Same(members[i]!,beforeRelease))continue;
            if(i<6||i+6>=nodes.Length||members[i+4] is not MethodInfo target||!target.IsStatic||target.GetParameters().Length!=3||
               members[i+6]==null||!ExactRuntimeIdentity.Same(members[i+6]!,afterRelease))throw new InvalidDataException("Release source hook lacks exact adjacent callee/after sequence");
            int Int(Instruction n)=>n.OpCode.Code==Code.Ldc_I4&&n.Operand is int value?value:throw new InvalidDataException("Source site metadata is not exact constant");
            int offset=Int(nodes[i-5]),targetPlan=Int(nodes[i-4]);
            for(int n=0;n<3;n++)
                if(nodes[i-3+n].OpCode.Code!=Code.Ldloc||nodes[i+1+n].OpCode.Code!=Code.Ldloc||
                   !ReferenceEquals(nodes[i-3+n].Operand,nodes[i+1+n].Operand))throw new InvalidDataException("Release source operands do not flow to exact adjacent callee");
            if(nodes[i+4].OpCode.Code!=Code.Call||!releaseSites.TryAdd((offset,targetPlan),target))throw new InvalidDataException("Release callsite duplicated or nonstatic");
        }
        var beforeFact=typeof(CompletionFactsHooks).GetMethod(nameof(CompletionFactsHooks.BeforeCall))!;
        var read=typeof(CompletionFactsHooks).GetMethod(nameof(CompletionFactsHooks.FieldRead))!;var write=typeof(CompletionFactsHooks).GetMethod(nameof(CompletionFactsHooks.FieldWritten))!;
        for(int i=0;i<nodes.Length;i++)
        {
            if(members[i]!=null&&ExactRuntimeIdentity.Same(members[i]!,beforeFact))
            {
                int Int(int n)=>nodes[n].OpCode.Code==Code.Ldc_I4&&nodes[n].Operand is int v?v:throw new InvalidDataException("Fact site is not an exact constant");
                if(i<6||nodes[i+1].OpCode.Code!=Code.Stloc)throw new InvalidDataException("Fact call token local absent");
                int site=Int(i-5),target=Int(i-3);int call=i+2;while(call<nodes.Length&&nodes[call].OpCode.Code is Code.Ldloc or Code.Ldloc_S)call++;
                if(call>=nodes.Length||nodes[call].OpCode.Code is not(Code.Call or Code.Callvirt or Code.Newobj)||members[call] is not MethodBase callee||!factSites.TryAdd((site,target),(callee,false,callee is MethodInfo methodInfo?methodInfo.ReturnType:callee.DeclaringType)))throw new InvalidDataException("Exact fact original call relation absent");
            }
            if(members[i]!=null&&(ExactRuntimeIdentity.Same(members[i]!,read)||ExactRuntimeIdentity.Same(members[i]!,write)))
            {
                var siteNode=nodes.Take(i).Reverse().Take(8).FirstOrDefault(n=>n.OpCode.Code==Code.Ldc_I4);var fieldNode=Enumerable.Range(0,i).Reverse().Take(20).FirstOrDefault(n=>members[n] is FieldInfo);
                if(siteNode?.Operand is not int site||members[fieldNode] is not FieldInfo field||!factFields.TryAdd(site,(field,field.FieldType)))throw new InvalidDataException("Field fact lacks exact source field");
            }
        }
    }
    internal static SourceBodyTemplate Capture(MethodDefinition method,int id,CallerSourceKind kind)
    {
        if(id<=0||method.HasGenericParameters)throw new NotSupportedException("Positive plan and closed method required");
        bool zeroCaller=kind==CallerSourceKind.Replacement&&method.Parameters.Count==0;
        if(kind!=CallerSourceKind.Observation&&(id<=0||method.HasThis||method.ReturnType.MetadataType!=MetadataType.Void||(!zeroCaller&&method.Parameters.Count!=3)||method.HasGenericParameters))
            throw new NotSupportedException("Only exact static three-operand targets or zero-operand Replacement callers");
        return new(method.Body,id,kind);
    }
    internal bool TryReleaseTarget(int offset,int plan,out MethodBase target)=>releaseSites.TryGetValue((offset,plan),out target!);
    internal bool TryFactTarget(int site,int plan,out MethodBase target,out bool receiverArgument,out Type? valueType){if(factSites.TryGetValue((site,plan),out var x)){target=x.Method;receiverArgument=x.ReceiverArgument;valueType=x.ValueType;return true;}target=null!;receiverArgument=false;valueType=null;return false;}
    internal bool TryFactField(int site,out FieldInfo field,out Type? valueType){if(factFields.TryGetValue(site,out var x)){field=x.Field;valueType=x.ValueType;return true;}field=null!;valueType=null;return false;}
    internal void ControlForwarder(MethodBase actualForwarder,MethodBase generatedOriginal)
    {
        foreach(var key in releaseSites.Keys.ToArray())
            if(ExactRuntimeIdentity.Same(releaseSites[key],actualForwarder))releaseSites[key]=generatedOriginal;
        foreach(var key in factSites.Keys.ToArray())if(ExactRuntimeIdentity.Same(factSites[key].Method,actualForwarder))factSites[key]=(generatedOriginal,actualForwarder.IsStatic&&!generatedOriginal.IsStatic,generatedOriginal is MethodInfo methodInfo?methodInfo.ReturnType:generatedOriginal.DeclaringType);
    }
    private static string Form(Instruction i,Func<Instruction,int> index,ControlFlowOperands flow)=>CilIdentity.Normalize(i.OpCode.Name,flow.Operand(i,index));
    internal Instruction RequireOriginal(Mono.Cecil.Cil.MethodBody body)
    {
        if(body.Variables.Count<locals.Length||locals.Where((t,i)=>!ExactRuntimeIdentity.Same(t,ExactRuntimeIdentity.ResolveType(body.Variables[i].VariableType))).Any())
            throw new InvalidDataException("Original exact locals changed");
        var all=body.Instructions.ToArray();var flow=new ControlFlowOperands(body);var starts=new List<int>();
        for(int start=0;start+forms.Length<=all.Length;start++)
        {
            int Index(Instruction i)=>flow.Position(i)-start;
            bool ok=true;
            for(int j=0;j<forms.Length;j++)
            {
                var ins=all[start+j];
                if(j==forms.Length-1&&ins.OpCode.Code is Code.Br or Code.Br_S&&Index(flow.Targets(ins)[0])>=forms.Length)continue;
                if(Form(ins,Index,flow)!=forms[j]){ok=false;break;}
                if(members[j] is {} expected&&(ins.Operand is not MemberReference actual||!ExactRuntimeIdentity.Same(expected,ExactRuntimeIdentity.Resolve(actual)))){ok=false;break;}
            }
            if(ok)starts.Add(start);
        }
        if(starts.Count!=1)throw new InvalidDataException("Original source body absent, changed or ambiguous");
        int first=starts[0],last=first+forms.Length;
        int Pos(Instruction? i)=>flow.Position(i,true);
        bool Inside(Instruction i)=>Pos(i)>=first&&Pos(i)<last;
        foreach(var i in all.Where(i=>!Inside(i)))
        {
            var targets=flow.Targets(i);
            if(targets.Any(t=>Inside(t)&&Pos(t)!=first))throw new InvalidDataException("Foreign control-flow enters source interior");
        }
        foreach(var expected in clauses)
        {
            bool Match(ExceptionHandler h)=>h.HandlerType==expected.Kind&&Pos(h.TryStart)==first+expected.Start&&Pos(h.TryEnd)==first+expected.End&&
                Pos(h.HandlerStart)==first+expected.Handler&&Pos(h.HandlerEnd)==first+expected.HandlerEnd&&
                (h.FilterStart==null?-1:Pos(h.FilterStart)-first)==expected.Filter&&
                (expected.Catch==null?h.CatchType==null:h.CatchType!=null&&ExactRuntimeIdentity.Same(expected.Catch,ExactRuntimeIdentity.ResolveType(h.CatchType)));
            if(body.ExceptionHandlers.Count(Match)!=1)throw new InvalidDataException("Original source EH changed");
        }
        foreach(var h in body.ExceptionHandlers)
        {
            int s=Pos(h.TryStart),e=Pos(h.TryEnd),hs=Pos(h.HandlerStart),he=Pos(h.HandlerEnd);
            bool own=clauses.Any(c=>s==first+c.Start&&e==first+c.End&&hs==first+c.Handler&&he==first+c.HandlerEnd);
            if(own)continue;
            if((s<last&&e>first)&&!(s<=first&&e>=last))throw new InvalidDataException("Foreign partial source try range");
            if(hs<last&&he>first||h.FilterStart!=null&&Pos(h.FilterStart)<last&&hs>first)throw new InvalidDataException("Source overlaps foreign handler/filter");
        }
        return all[first+enterIndex];
    }
}


