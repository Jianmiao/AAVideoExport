using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Reflection;
namespace Aave.WaitOwnership.CallerSource;
internal static class FactShape
{
    internal static bool Supported(Type t,bool returns)=>t==typeof(void)?returns:!t.IsByRef&&!t.IsPointer&&!t.IsByRefLike&&!t.ContainsGenericParameters&&
        (!t.IsValueType||t==typeof(bool)||t==typeof(int)||t==typeof(long)||t==typeof(float)||t==typeof(double)||t.IsEnum&&Enum.GetUnderlyingType(t)==typeof(int));
    internal static IEnumerable<Instruction> Box(ModuleDefinition module,TypeReference type)
    {
        string? hook=type.MetadataType switch{MetadataType.Boolean=>nameof(CompletionFactsHooks.BoxBoolean),MetadataType.Int32=>nameof(CompletionFactsHooks.BoxInt32),MetadataType.Int64=>nameof(CompletionFactsHooks.BoxInt64),MetadataType.Single=>nameof(CompletionFactsHooks.BoxSingle),MetadataType.Double=>nameof(CompletionFactsHooks.BoxDouble),_=>null};
        if(hook==null&&type.IsValueType){var definition=type.Resolve();if(definition?.IsEnum!=true||definition.Fields.Single(f=>f.Name=="value__").FieldType.MetadataType!=MetadataType.Int32)throw new NotSupportedException("Only verified Int32 enums are transported");yield return Instruction.Create(OpCodes.Conv_I4);hook=nameof(CompletionFactsHooks.BoxInt32);}
        if(hook!=null)yield return Instruction.Create(OpCodes.Call,module.ImportReference(typeof(CompletionFactsHooks).GetMethod(hook)!));
    }
    internal static Instruction Receiver(MethodDefinition m,bool originalInstance)=>originalInstance?(m.HasThis?Instruction.Create(OpCodes.Ldarg_0):Instruction.Create(OpCodes.Ldarg,m.Parameters[0])):Instruction.Create(OpCodes.Ldnull);
    internal static IEnumerable<Instruction> Arguments(MethodDefinition m,bool originalInstance)
    {
        int shift=originalInstance&&!m.HasThis?1:0;int count=m.Parameters.Count-shift;
        yield return Instruction.Create(OpCodes.Ldc_I4,count);yield return Instruction.Create(OpCodes.Call,m.Module.ImportReference(typeof(CompletionFactsHooks).GetMethod(nameof(CompletionFactsHooks.NewArguments))!));
        for(int i=0;i<count;i++){yield return Instruction.Create(OpCodes.Dup);yield return Instruction.Create(OpCodes.Ldc_I4,i);yield return Instruction.Create(OpCodes.Ldarg,m.Parameters[i+shift]);foreach(var ins in Box(m.Module,m.Parameters[i+shift].ParameterType))yield return ins;yield return Instruction.Create(OpCodes.Call,m.Module.ImportReference(typeof(CompletionFactsHooks).GetMethod(nameof(CompletionFactsHooks.SetArgument))!));}
    }
}
