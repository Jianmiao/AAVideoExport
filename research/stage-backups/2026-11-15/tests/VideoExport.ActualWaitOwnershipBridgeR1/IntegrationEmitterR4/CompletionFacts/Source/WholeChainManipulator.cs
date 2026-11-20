using System.Reflection;

using Mono.Cecil.Cil;
using MonoMod.Cil;
using ManagedOriginalCallWitness;
namespace Aave.WaitOwnership.CallerSource;
public static class WholeChainManipulator
{
    public static void Apply(ILContext context,MethodBase original)
    {
        if(!ManagedChainRegistration.TryGet(original,out var registration))return;
        var generation=registration.BeginBuild();
        try
        {
            var body=context.Body;var method=context.Method;
            var sourceEnter=registration.Template.RequireOriginal(body);
            if(body.Instructions.Any(i=>i.OpCode.Code is Code.Jmp or Code.Tail))throw new NotSupportedException("Whole-chain transfer unsupported");
            // Source validation happens before edits. Final expectation capture
            // below can still refuse after edits; this is not a rollback. Such
            // hooks stay observational because the generation is never active.
            var existing=body.Instructions.ToArray();var first=existing[0];
            var chain=new VariableDefinition(context.Module.ImportReference(typeof(CallerSourceHooks.ChainFrame)));
            body.Variables.Add(chain);
            var il=body.GetILProcessor();
            il.InsertBefore(sourceEnter,Instruction.Create(OpCodes.Ldc_I8,generation.Token));
            sourceEnter.Operand=context.Module.ImportReference(typeof(CallerSourceHooks).GetMethod(registration.Kind==CallerSourceKind.Observation?nameof(CallerSourceHooks.EnterGeneralBound):nameof(CallerSourceHooks.EnterBound),BindingFlags.Static|BindingFlags.NonPublic)!);
            var entry=new List<Instruction>{Instruction.Create(OpCodes.Ldc_I8,generation.Token)};
            if(registration.Kind==CallerSourceKind.Observation)
            {
                entry.Add(FactShape.Receiver(method,!registration.Original.IsStatic));entry.AddRange(FactShape.Arguments(method,!registration.Original.IsStatic));
            }
            else if(registration.Kind==CallerSourceKind.Replacement&&method.Parameters.Count==0)
            {
                // Zero-argument caller entry has no player/animation/reason
                // operands. Actual identities arrive only at source callsites.
                entry.AddRange(new[]{Instruction.Create(OpCodes.Ldnull),Instruction.Create(OpCodes.Ldnull),Instruction.Create(OpCodes.Ldnull)});
            }
            else
            {
                entry.Add(Instruction.Create(OpCodes.Ldarg,method.Parameters[0]));entry.Add(Instruction.Create(OpCodes.Ldarg,method.Parameters[1]));
                entry.Add(method.Parameters[2].ParameterType.FullName==typeof(string).FullName?Instruction.Create(OpCodes.Ldarg,method.Parameters[2]):Instruction.Create(OpCodes.Ldnull));
            }
            entry.Add(Instruction.Create(OpCodes.Call,context.Module.ImportReference(typeof(CallerSourceHooks).GetMethod(registration.Kind==CallerSourceKind.Observation?nameof(CallerSourceHooks.EnterGeneralChain):nameof(CallerSourceHooks.EnterChain),BindingFlags.Static|BindingFlags.NonPublic)!)));
            entry.Add(Instruction.Create(OpCodes.Stloc,chain));
            // The extra nop gives the outer try a distinct entry from an existing
            // Harmony try at the old first node. All old branches/EH retain nodes.
            var tryStart=Instruction.Create(OpCodes.Nop);entry.Add(tryStart);
            foreach(var n in entry)il.InsertBefore(first,n);
            var cleanup=Instruction.Create(OpCodes.Ldloc,chain);VariableDefinition? result=null;
            if(method.ReturnType.MetadataType!=Mono.Cecil.MetadataType.Void){result=new VariableDefinition(method.ReturnType);body.Variables.Add(result);}
            var done=result==null?Instruction.Create(OpCodes.Ret):Instruction.Create(OpCodes.Ldloc,result);
            foreach(var h in body.ExceptionHandlers){h.TryEnd??=cleanup;h.HandlerEnd??=cleanup;}
            foreach(var ret in existing.Where(i=>i.OpCode.Code==Code.Ret))
            {if(result==null){ret.OpCode=OpCodes.Leave;ret.Operand=done;}else{ret.OpCode=OpCodes.Stloc;ret.Operand=result;il.InsertAfter(ret,Instruction.Create(OpCodes.Leave,done));}}
            il.Append(cleanup);il.Append(Instruction.Create(OpCodes.Call,context.Module.ImportReference(typeof(CallerSourceHooks).GetMethod(nameof(CallerSourceHooks.ExitChain),BindingFlags.Static|BindingFlags.NonPublic)!)));
            il.Append(Instruction.Create(OpCodes.Endfinally));il.Append(done);if(result!=null)il.Append(Instruction.Create(OpCodes.Ret));
            body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally){TryStart=tryStart,TryEnd=cleanup,HandlerStart=cleanup,HandlerEnd=done});
            body.MaxStackSize=checked(body.MaxStackSize+5);
            // Freeze final expected form now; later IL manipulators are checked
            // against this independent immutable snapshot at normal publication.
            generation.Expected=DynamicBodyExpectation.Capture(body,CilIdentity.Cecil(body),generation.Token);
            generation.PreparationStatus="Exact source and independent final expectation prepared";
            generation.PublicationStatus="Awaiting actual publication";
            generation.Reason="Awaiting exact actual normal generation/materialization and publication";
        }
        catch(Exception e)
        {
            // No qualification when transformation or proof fails. Unknown
            // original forms were refused before edits. The original pipeline
            // still executes; any inserted hooks remain observational.
            generation.PreparationFailure??=e.GetType().Name+": "+e.Message;
            generation.PreparationStatus="Failed";
            generation.Reason=generation.PreparationFailure;
        }
    }
}


