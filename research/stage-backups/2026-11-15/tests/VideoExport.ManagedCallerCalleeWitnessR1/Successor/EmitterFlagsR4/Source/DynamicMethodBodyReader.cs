using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using Mono.Cecil;
using MonoMod.Utils;

namespace ManagedOriginalCallWitness;

// An expectation is input evidence, never reported as a CLR readback. The
// locals encoding is independently produced by the public CLR SignatureHelper
// from every exact expected local Type and pinned flag; no pointer is read as
// a Type, and no target is generated/materialized/invoked by this operation.
internal sealed record DynamicBodyExpectation(long Token, string[] Forms, byte[] Locals,
    int LocalCount, bool InitLocals)
{
    private readonly MemberInfo[]? exactMembers;
    internal DynamicBodyExpectation(long token,string[] forms,byte[] locals,int localCount,bool initLocals,MemberInfo[] members)
        :this(token,forms,locals,localCount,initLocals)=>exactMembers=members.ToArray();
    internal void RequireMembers(MemberInfo[] actual)
    {
        if(exactMembers is null)throw new InvalidDataException("Missing independent exact runtime member expectation.");
        if(exactMembers.Length!=actual.Length)throw new InvalidDataException("Exact runtime member count differs.");
        for(int i=0;i<actual.Length;i++)
            if(!ExactRuntimeIdentity.Same(exactMembers[i],actual[i]))
                throw new InvalidDataException("Exact runtime member identity differs at operand "+i+" (including EH catch context).");
    }
    internal string[] GeneratorForms { get; init; } = Forms;
    internal bool GeneratorInitLocals { get; init; } = InitLocals;
    internal static DynamicBodyExpectation Capture(Mono.Cecil.Cil.MethodBody body, string[] forms, long token)
    {
        DynamicEmitExpectation.RequirePinned();
        var signature = SignatureHelper.GetLocalVarSigHelper();
        foreach (var local in body.Variables)
        {
            TypeReference type = local.VariableType;
            bool pinned = type is PinnedType;
            if (pinned) type = ((PinnedType)type).ElementType;
            if (type is RequiredModifierType or OptionalModifierType or SentinelType or FunctionPointerType)
                throw new NotSupportedException("Local custom modifier, sentinel or function pointer requires a separate exact signature proof.");
            signature.AddArgument(ExactRuntimeIdentity.ResolveType(type), pinned);
        }
        var members=new List<MemberInfo>();
        foreach(var instruction in body.Instructions)
            if(instruction.Operand is MemberReference member)
            {
                if(ReferenceEquals(member,body.Method))throw new NotSupportedException("Self token has no exact generated entity before normal Generate.");
                members.Add(ExactRuntimeIdentity.Resolve(member));
            }
        foreach(var clause in body.ExceptionHandlers)
            if(clause.CatchType is not null)members.Add(ExactRuntimeIdentity.ResolveType(clause.CatchType));
        return new(token, forms.ToArray(), signature.GetSignature(), body.Variables.Count, body.InitLocals,members.ToArray())
            { GeneratorForms=DynamicEmitExpectation.Predict(body,forms),GeneratorInitLocals=PinnedEmitterFlags.GeneratorInitLocals() };
    }
    internal byte[] GeneratorLocals()
    {
        // Exact InternalGetSignatureArray allocation in both pinned CoreLibs.
        // It appends one zero normally; its strict thresholds leave extra zero
        // padding at 127 and 16383. These bytes are compared, not normalized.
        int padding = LocalCount == 127 ? 2 : LocalCount == 16383 ? 3 : 1;
        return Locals.Concat(new byte[padding]).ToArray();
    }
}

internal sealed record ClrExceptionClause(int Flags, int TryOffset, int TryLength,
    int HandlerOffset, int HandlerLength, int ClassTokenOrFilterOffset);

internal sealed class DynamicBodyReadback
{
    private readonly DynamicMethod method;
    private readonly Delegate? callable;
    private readonly IntPtr publishedHandleValue;
    private readonly object? publishedWrapper;
    private readonly object resolver, scope, producer, handle;
    private readonly object[] tokens;
    private readonly object[] frozenTokens;
    private readonly MemberInfo[] exactMembers;
    private readonly byte[] code, locals;
    private readonly ClrExceptionClause[] clauses;
    private readonly long token;
    private readonly bool fromInfo;
    private readonly bool initLocals;
    private readonly int stack;
    internal string[] Forms { get; }
    internal string CodeSha256 { get; }
    internal string Provenance { get; }
    internal object ResolverIdentity => resolver;
    internal object ScopeIdentity => scope;
    internal object ProducerIdentity => producer;
    internal object MethodHandleIdentity => handle;
    internal byte[] LocalSignature => locals.ToArray();
    internal byte[] Code => code.ToArray();
    internal ClrExceptionClause[] ExceptionClauses => clauses.ToArray();
    internal DynamicBodyReadback(DynamicMethod method, Delegate? callable, object resolver, object scope,
        object producer, object handle, object[] tokens, byte[] code, byte[] locals,
        ClrExceptionClause[] clauses, long token, bool fromInfo, bool initLocals, int stack)
    {
        publishedWrapper=callable==null?DynamicMethodBodyReader.Field(method,"m_dynMethod"):null;
        publishedHandleValue=callable==null?DynamicMethodBodyReader.PublishedHandleValue(handle):IntPtr.Zero;
        this.method=method;this.callable=callable;this.resolver=resolver;this.scope=scope;
        this.producer=producer;this.handle=handle;this.tokens=tokens;this.code=code;this.locals=locals;
        frozenTokens=tokens.Select(value=>value is byte[] blob?(object)blob.ToArray():value).ToArray();
        this.clauses=clauses;this.token=token;this.fromInfo=fromInfo;this.initLocals=initLocals;this.stack=stack;
        var observed=new List<MemberInfo>();
        Forms=DynamicMethodBodyReader.Decode(code, clauses, (value,kind)=>
        {var resolved=Resolve(value,kind);if(resolved is MemberInfo member)observed.Add(member);return resolved;});
        exactMembers=observed.ToArray();
        CodeSha256=Convert.ToHexString(SHA256.HashData(code));
        Provenance=$"CoreLib:{DynamicMethodBodyReader.CoreLibHash};{(callable==null?"ActualPublishedTarget":"GenerateObject→DelegateOwner")}→MethodHandleKeepalive→Resolver→{(fromInfo?"DynamicILInfo":"DynamicILGenerator")}Scope;build:{token};code:{CodeSha256};locals:{Convert.ToHexString(SHA256.HashData(locals))};EH:{clauses.Length};maxstack:{stack}";
    }
    internal object Resolve(int value, OperandType kind) => DynamicMethodBodyReader.ResolveToken(frozenTokens, value, kind);
    internal void RequireExact(MethodInfo exactMethod, Delegate exactCallable, DynamicBodyExpectation expected)
    {
        if (!ReferenceEquals(method,exactMethod) || !ReferenceEquals(callable,exactCallable) || token!=expected.Token)
            throw new InvalidDataException("Exact generated method/delegate/build token differs.");
        DynamicMethodBodyReader.RequireChain(method, exactCallable, resolver, scope, producer, handle, fromInfo);
        RequireBody(expected);
    }
    internal void RequirePublishedExact(MethodInfo exactActualTarget,DynamicBodyExpectation expected)
    {
        if(callable!=null||!ReferenceEquals(method,exactActualTarget)||token!=expected.Token)
            throw new InvalidDataException("Exact actual published target/build token differs; no delegate attestation substituted.");
        DynamicMethodBodyReader.RequirePublishedChain(method,resolver,scope,producer,handle,fromInfo);
        if(!ReferenceEquals(publishedWrapper,DynamicMethodBodyReader.Field(method,"m_dynMethod"))||DynamicMethodBodyReader.PublishedHandleValue(handle)!=publishedHandleValue)
            throw new InvalidDataException("Existing published method handle changed during readback.");
        RequireBody(expected);
    }
    private void RequireBody(DynamicBodyExpectation expected)
    {
        if (initLocals!=method.InitLocals || initLocals!=(fromInfo?expected.InitLocals:expected.GeneratorInitLocals))
            throw new InvalidDataException("Final CLR InitLocals differs.");
        if (!code.SequenceEqual(DynamicMethodBodyReader.Bytes(resolver,"m_code")) ||
            !locals.SequenceEqual(DynamicMethodBodyReader.Bytes(resolver,"m_localSignature")) ||
            stack!=DynamicMethodBodyReader.Value<int>(resolver,"m_stackSize") ||
            !clauses.SequenceEqual(DynamicMethodBodyReader.Clauses(resolver)))
            throw new InvalidDataException("CLR resolver body changed during publication readback.");
        var current=DynamicMethodBodyReader.ScopeTokens(scope);
        if (current.Length!=tokens.Length || tokens.Where((item,index)=>!ReferenceEquals(item,current[index])).Any())
            throw new InvalidDataException("Exact dynamic token scope changed during publication readback.");
        for(int i=1;i<current.Length;i++)
            if(frozenTokens[i] is byte[] signature && (current[i] is not byte[] actual || !signature.SequenceEqual(actual)))
                throw new InvalidDataException("Dynamic signature token contents changed during publication readback.");
        byte[] expectedLocals=fromInfo?expected.Locals:expected.GeneratorLocals();
        if (!locals.SequenceEqual(expectedLocals))
            throw new InvalidDataException("Final CLR locals signature differs: expected "+Convert.ToHexString(expectedLocals)+" actual "+Convert.ToHexString(locals));
        var predicted=fromInfo?expected.Forms:expected.GeneratorForms;
        expected.RequireMembers(exactMembers);
        if (!predicted.SequenceEqual(Forms))
        {
            int mismatch=Enumerable.Range(0,Math.Min(predicted.Length,Forms.Length)).FirstOrDefault(i=>predicted[i]!=Forms[i],-1);
            throw new InvalidDataException("Final predicted/CLR code or EH differs at "+mismatch+": expected="+(mismatch<0?predicted.Length:predicted[mismatch])+" actual="+(mismatch<0?Forms.Length:Forms[mismatch]));
        }
    }
}

internal static class DynamicMethodBodyReader
{
    // These exact binary pins and every field/read path are cold-verified in
    // ClrMetadataAudit. Unsupported CoreLib is refused before private reads.
    private const string Core636="3CE6F27555E6CE2DC1D314012A5798A1A62B4527813C085548F7D937F83DA74A";
    private const string Core607="2CAA46D2AE726E135592033B6C682232A08698C23DBD1A9207F86C4CCA081AD7";
    private static readonly Lazy<string> CheckedCore=new(CheckCore);
    private static readonly Dictionary<short,OpCode> Ops=typeof(System.Reflection.Emit.OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static)
        .Where(f=>f.FieldType==typeof(OpCode)).Select(f=>(OpCode)f.GetValue(null)!).ToDictionary(o=>o.Value);
    internal static string CoreLibHash=>CheckedCore.Value;
    private static string CheckCore()
    {
        var assembly=typeof(DynamicMethod).Assembly;
        string hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)));
        if (IntPtr.Size!=8 || hash is not (Core636 or Core607)) throw new NotSupportedException("Unverified CLR binary/architecture.");
        Guid expected=Guid.Parse(hash==Core636?"e300fc3b-cf25-47fd-9aa6-7e2916204333":"ae1f29ab-81b3-4e90-9584-423a3fc6fff7");
        if(assembly.ManifestModule.ModuleVersionId!=expected)throw new InvalidDataException("CoreLib identity differs.");
        return hash;
    }
    internal static object? Field(object obj,string name)
    {
        _=CoreLibHash;
        for(Type? type=obj.GetType();type is not null;type=type.BaseType)
        {
            if(type.Assembly!=typeof(DynamicMethod).Assembly)throw new InvalidDataException("Private CLR field receiver belongs to another assembly.");
            var field=type.GetField(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly);
            if(field is not null)return field.GetValue(obj);
        }
        throw new MissingFieldException(obj.GetType().FullName,name);
    }
    internal static T Value<T>(object obj,string name)=>Field(obj,name) is T value?value:throw new InvalidDataException("Missing/wrong CLR field "+name);
    internal static byte[] Bytes(object obj,string name)=>Value<byte[]>(obj,name).ToArray();
    private static void TypeIs(object obj,string name)
    {if(obj.GetType()!=typeof(DynamicMethod).Assembly.GetType(name,true))throw new InvalidDataException("Wrong CLR object type: "+name);}
    internal static DynamicBodyReadback Read(MethodInfo exactMethod,Delegate existingCallable,long buildToken)
    {
        _=CoreLibHash;
        if(exactMethod is not DynamicMethod method || exactMethod.GetType()!=typeof(DynamicMethod))
            throw new NotSupportedException("The exact Generate result is not this CLR's DynamicMethod.");
        object resolver=Field(method,"m_resolver")??throw new InvalidDataException("No resolver from normal materialization.");
        object handle=Field(method,"m_methodHandle")??throw new InvalidDataException("No normally materialized method handle.");
        object scope=Field(resolver,"m_scope")??throw new InvalidDataException("No dynamic token scope.");
        object? info=Field(method,"m_DynamicILInfo");
        bool fromInfo=info is not null;
        object producer=info??Field(method,"m_ilGenerator")??throw new InvalidDataException("No exact method producer.");
        RequireChain(method,existingCallable,resolver,scope,producer,handle,fromInfo);
        return new(method,existingCallable,resolver,scope,producer,handle,ScopeTokens(scope),Bytes(resolver,"m_code"),
            Bytes(resolver,"m_localSignature"),Clauses(resolver),buildToken,fromInfo,method.InitLocals,Value<int>(resolver,"m_stackSize"));
    }
    // Read only after the pinned stock provider returns its exact applied target.
    // No delegate creation, resolver invocation, GetMethodDescriptor, Prepare,
    // Bake, native-address lookup or target execution occurs in this path.
    internal static DynamicBodyReadback ReadPublished(MethodInfo actualTarget,long buildToken)
    {
        _=CoreLibHash;
        if(actualTarget is not DynamicMethod method||actualTarget.GetType()!=typeof(DynamicMethod))
            throw new NotSupportedException("Actual published target is not this pinned CLR DynamicMethod.");
        object handle=Field(method,"m_methodHandle")??throw new InvalidDataException("Published target has no already-materialized method handle; reader will not create one.");
        object resolver=Field(method,"m_resolver")??throw new InvalidDataException("Published target has no existing resolver.");
        object scope=Field(resolver,"m_scope")??throw new InvalidDataException("Published target has no existing scope.");
        object? info=Field(method,"m_DynamicILInfo");bool fromInfo=info!=null;
        object producer=info??Field(method,"m_ilGenerator")??throw new InvalidDataException("Published target has no exact producer.");
        RequirePublishedChain(method,resolver,scope,producer,handle,fromInfo);
        return new(method,null,resolver,scope,producer,handle,ScopeTokens(scope),Bytes(resolver,"m_code"),Bytes(resolver,"m_localSignature"),Clauses(resolver),buildToken,fromInfo,method.InitLocals,Value<int>(resolver,"m_stackSize"));
    }
    internal static IntPtr PublishedHandleValue(object handle)
    {
        TypeIs(handle,"System.RuntimeMethodInfoStub");
        object value=Field(handle,"m_value")??throw new InvalidDataException("Existing handle stub lacks m_value.");
        TypeIs(value,"System.RuntimeMethodHandleInternal");
        IntPtr pointer=Value<IntPtr>(value,"m_handle");
        if(pointer==IntPtr.Zero)throw new InvalidDataException("Existing method handle is zero; reader will not materialize it.");
        return pointer; // nonzero/stability identity only; never dereferenced
    }
    internal static void RequirePublishedChain(DynamicMethod method,object resolver,object scope,object producer,object handle,bool fromInfo)
    {
        TypeIs(resolver,"System.Reflection.Emit.DynamicResolver");TypeIs(scope,"System.Reflection.Emit.DynamicScope");
        TypeIs(producer,fromInfo?"System.Reflection.Emit.DynamicILInfo":"System.Reflection.Emit.DynamicILGenerator");
        _=PublishedHandleValue(handle);
        object wrapper=Field(method,"m_dynMethod")??throw new InvalidDataException("Missing exact method wrapper.");
        TypeIs(wrapper,"System.Reflection.Emit.DynamicMethod+RTDynamicMethod");
        if(!ReferenceEquals(Field(wrapper,"m_owner"),method)||
           !ReferenceEquals(Field(method,"m_resolver"),resolver)||!ReferenceEquals(Field(resolver,"m_method"),method)||
           !ReferenceEquals(Field(method,"m_methodHandle"),handle)||!ReferenceEquals(Field(handle,"m_keepalive"),resolver)||
           !ReferenceEquals(Field(resolver,"m_scope"),scope)||!ReferenceEquals(Field(producer,"m_scope"),scope)||
           !ReferenceEquals(Field(method,fromInfo?"m_DynamicILInfo":"m_ilGenerator"),producer)||
           !ReferenceEquals(Field(producer,fromInfo?"m_method":"m_methodBuilder"),method))
            throw new InvalidDataException("Exact published target/handle keepalive/resolver/producer/scope back-references differ.");
        if(fromInfo&&(!ReferenceEquals(Field(resolver,"m_code"),Field(producer,"m_code"))||
           !ReferenceEquals(Field(resolver,"m_localSignature"),Field(producer,"m_localSignature"))||
           !ReferenceEquals(Field(resolver,"m_exceptionHeader"),Field(producer,"m_exceptions"))))
            throw new InvalidDataException("Exact DynamicILInfo resolver arrays differ.");
    }
    internal static void RequireChain(DynamicMethod method,Delegate callable,object resolver,object scope,
        object producer,object handle,bool fromInfo)
    {
        TypeIs(resolver,"System.Reflection.Emit.DynamicResolver");TypeIs(scope,"System.Reflection.Emit.DynamicScope");
        TypeIs(producer,fromInfo?"System.Reflection.Emit.DynamicILInfo":"System.Reflection.Emit.DynamicILGenerator");
        TypeIs(handle,"System.RuntimeMethodInfoStub");
        object delegateMethod=callable.Method;
        TypeIs(delegateMethod,"System.Reflection.Emit.DynamicMethod+RTDynamicMethod");
        if(!ReferenceEquals(Field(delegateMethod,"m_owner"),method) || !ReferenceEquals(Field(method,"m_dynMethod"),delegateMethod) ||
            !ReferenceEquals(Field(method,"m_resolver"),resolver) || !ReferenceEquals(Field(resolver,"m_method"),method) ||
            !ReferenceEquals(Field(method,"m_methodHandle"),handle) || !ReferenceEquals(Field(handle,"m_keepalive"),resolver) ||
            !ReferenceEquals(Field(resolver,"m_scope"),scope) || !ReferenceEquals(Field(producer,"m_scope"),scope) ||
            !ReferenceEquals(Field(method,fromInfo?"m_DynamicILInfo":"m_ilGenerator"),producer) ||
            !ReferenceEquals(Field(producer,fromInfo?"m_method":"m_methodBuilder"),method))
            throw new InvalidDataException("Exact method/delegate/resolver/scope/producer/handle back-reference chain differs.");
        if(fromInfo)
        {
            if(!ReferenceEquals(Field(resolver,"m_code"),Field(producer,"m_code")) ||
                !ReferenceEquals(Field(resolver,"m_localSignature"),Field(producer,"m_localSignature")) ||
                !ReferenceEquals(Field(resolver,"m_exceptionHeader"),Field(producer,"m_exceptions")))
                throw new InvalidDataException("DynamicILInfo no longer owns the resolver's exact frozen arrays.");
        }
    }
    internal static object[] ScopeTokens(object scope)
    {
        TypeIs(scope,"System.Reflection.Emit.DynamicScope");
        var list=Value<List<object>>(scope,"m_tokens");
        if(list.Count==0 || list[0] is not null)throw new InvalidDataException("Invalid dynamic scope null token.");
        return list.ToArray();
    }
    internal static object ResolveToken(object[] tokens,int token,OperandType kind)
    {
        int index=token&0x00ffffff, table=(int)((uint)token>>24);
        if(index<=0 || index>=tokens.Length)throw new InvalidDataException("Dynamic scope token index out of range.");
        object value=tokens[index]??throw new InvalidDataException("Null dynamic scope token.");
        if(kind==OperandType.InlineString)return table==0x70 && value is string s?s:throw new InvalidDataException("String token kind differs.");
        if(kind==OperandType.InlineSig)return table==0x11 && value is byte[] sig?new SignatureOperand(sig.ToArray()):throw new InvalidDataException("Signature token kind differs.");
        MemberInfo member;
        if(value is RuntimeTypeHandle th && table==0x02)member=Type.GetTypeFromHandle(th)!;
        else if(value is RuntimeMethodHandle mh && table==0x06)member=MethodBase.GetMethodFromHandle(mh)!;
        else if(value is RuntimeFieldHandle fh && table==0x04)member=FieldInfo.GetFieldFromHandle(fh);
        else if(value.GetType()==typeof(DynamicMethod).Assembly.GetType("System.Reflection.Emit.GenericMethodInfo") && table==0x06)
            member=MethodBase.GetMethodFromHandle(Value<RuntimeMethodHandle>(value,"m_methodHandle"),Value<RuntimeTypeHandle>(value,"m_context"))!;
        else if(value.GetType()==typeof(DynamicMethod).Assembly.GetType("System.Reflection.Emit.GenericFieldInfo") && table==0x04)
            member=FieldInfo.GetFieldFromHandle(Value<RuntimeFieldHandle>(value,"m_fieldHandle"),Value<RuntimeTypeHandle>(value,"m_context"));
        else throw new NotSupportedException("Unsupported or mismatched dynamic token kind "+table.ToString("X2")+"/"+value.GetType().FullName+"; dynamic-method and vararg targets are never materialized by this reader.");
        bool valid=kind switch { OperandType.InlineType=>member is Type, OperandType.InlineMethod=>member is MethodBase,
            OperandType.InlineField=>member is FieldInfo,OperandType.InlineTok=>true,_=>false };
        if(!valid)throw new InvalidDataException("Dynamic token member/operand kind differs.");
        return member;
    }
    internal static ClrExceptionClause[] Clauses(object resolver)
    {
        byte[]? raw=Field(resolver,"m_exceptionHeader") as byte[];
        if(raw is {Length:>0})return RawClauses(raw);
        if(Field(resolver,"m_exceptions") is not Array items)return Array.Empty<ClrExceptionClause>();
        var result=new List<ClrExceptionClause>();
        foreach(object item in items)
        {
            TypeIs(item,"System.Reflection.Emit.__ExceptionInfo");
            int count=Value<int>(item,"m_currentCatch"),start=Value<int>(item,"m_startAddr");
            int[] flags=Value<int[]>(item,"m_type"),handlers=Value<int[]>(item,"m_catchAddr"),ends=Value<int[]>(item,"m_catchEndAddr"),filters=Value<int[]>(item,"m_filterAddr");
            if(count<0 || new[]{flags.Length,handlers.Length,ends.Length,filters.Length}.Any(n=>n<count))throw new InvalidDataException("Malformed CLR exception arrays.");
            for(int i=0;i<count;i++)result.Add(new(flags[i],start,Value<int>(item,(flags[i]&2)!=0?"m_endFinally":"m_endAddr")-start,handlers[i],ends[i]-handlers[i],filters[i]));
        }
        return result.ToArray();
    }
    private static ClrExceptionClause[] RawClauses(byte[] raw)
    {
        if(raw.Length<4 || (raw[0]&0x3f)!=1 || (raw[0]&0x80)!=0)throw new NotSupportedException("Unsupported raw EH header/sections.");
        bool fat=(raw[0]&0x40)!=0;int size=fat?raw[1]|raw[2]<<8|raw[3]<<16:raw[1],width=fat?24:12;
        if(size!=raw.Length || (size-4)%width!=0 || (!fat && (raw[2]!=0 || raw[3]!=0)))throw new InvalidDataException("Malformed raw EH length/reserved bytes.");
        var result=new List<ClrExceptionClause>();
        for(int at=4;at<size;at+=width)
            result.Add(fat?new(BitConverter.ToInt32(raw,at),BitConverter.ToInt32(raw,at+4),BitConverter.ToInt32(raw,at+8),BitConverter.ToInt32(raw,at+12),BitConverter.ToInt32(raw,at+16),BitConverter.ToInt32(raw,at+20)):
                new(BitConverter.ToUInt16(raw,at),BitConverter.ToUInt16(raw,at+2),raw[at+4],BitConverter.ToUInt16(raw,at+5),raw[at+7],BitConverter.ToInt32(raw,at+8)));
        return result.ToArray();
    }
    private sealed record Branch(int Offset);
    private sealed record SignatureOperand(byte[] Bytes);
    internal static string[] Decode(byte[] il,ClrExceptionClause[] clauses,Func<int,OperandType,object> resolve)
    {
        var items=new List<(int Offset,string Op,object? Operand)>();int at=0;
        void Remaining(int n){if(n<0 || at>il.Length-n)throw new InvalidDataException("Truncated CLR IL operand.");}
        int I4(){Remaining(4);int v=BitConverter.ToInt32(il,at);at+=4;return v;}
        while(at<il.Length)
        {
            int offset=at;short key=il[at++];if(key==0xfe){Remaining(1);key=(short)(0xfe00|il[at++]);}
            if(!Ops.TryGetValue(key,out var op))throw new InvalidDataException("Unknown CLR opcode.");object? operand=null;
            switch(op.OperandType)
            {
                case OperandType.InlineNone:break;
                case OperandType.ShortInlineI:Remaining(1);operand=(sbyte)il[at++];break;
                case OperandType.InlineI:operand=I4();break;
                case OperandType.InlineI8:Remaining(8);operand=BitConverter.ToInt64(il,at);at+=8;break;
                case OperandType.ShortInlineR:Remaining(4);operand=BitConverter.ToSingle(il,at);at+=4;break;
                case OperandType.InlineR:Remaining(8);operand=BitConverter.ToDouble(il,at);at+=8;break;
                case OperandType.ShortInlineVar:Remaining(1);operand=(int)il[at++];break;
                case OperandType.InlineVar:Remaining(2);operand=(int)BitConverter.ToUInt16(il,at);at+=2;break;
                case OperandType.ShortInlineBrTarget:Remaining(1);int d=(sbyte)il[at++];operand=new Branch(checked(at+d));break;
                case OperandType.InlineBrTarget:int delta=I4();operand=new Branch(checked(at+delta));break;
                case OperandType.InlineSwitch:int n=I4();Remaining(checked(n*4));int end=at+n*4;operand=Enumerable.Range(0,n).Select(_=>checked(end+I4())).ToArray();break;
                case OperandType.InlineString:case OperandType.InlineField:case OperandType.InlineMethod:case OperandType.InlineType:case OperandType.InlineTok:case OperandType.InlineSig:operand=resolve(I4(),op.OperandType);break;
                default:throw new NotSupportedException("Unsupported CLR operand "+op.OperandType);
            }
            items.Add((offset,op.Name!,operand));
        }
        int Index(int offset){int index=offset==il.Length?items.Count:items.FindIndex(i=>i.Offset==offset);if(index<0)throw new InvalidDataException("CLR branch/EH boundary is not an instruction boundary.");return index;}
        string Format((int Offset,string Op,object? Operand) i)=>i.Operand switch {
            MemberInfo m=>CilIdentity.Member(m),Branch b=>"target:"+Index(b.Offset),int[] a=>"targets:"+string.Join(",",a.Select(Index)),
            SignatureOperand s=>"signature:"+Convert.ToHexString(s.Bytes),
            _ when i.Op.StartsWith("ldloc")||i.Op.StartsWith("stloc")=>i.Operand is null?"":"local:"+i.Operand,
            _ when i.Op.StartsWith("ldarg")||i.Op.StartsWith("starg")=>i.Operand is null?"":"arg:"+i.Operand,
            _=>CilIdentity.Operand(i.Operand)};
        var result=items.Select(i=>CilIdentity.Normalize(i.Op,Format(i))).ToList();
        foreach(var h in clauses)
        {
            string kind=h.Flags switch {0=>"Catch",1=>"Filter",2=>"Finally",4=>"Fault",_=>throw new NotSupportedException("Unsupported CLR EH flags.")};
            if(h.TryLength<=0 || h.HandlerLength<=0)throw new InvalidDataException("Empty/reversed CLR EH range.");
            string catchType=h.Flags==0?CilIdentity.Member((Type)resolve(h.ClassTokenOrFilterOffset,OperandType.InlineType)):"";
            result.Add($"EH:{kind}:{Index(h.TryOffset)}:{Index(checked(h.TryOffset+h.TryLength))}:{Index(h.HandlerOffset)}:{Index(checked(h.HandlerOffset+h.HandlerLength))}:{(h.Flags==1?Index(h.ClassTokenOrFilterOffset):-1)}:{catchType}");
        }
        return result.ToArray();
    }
}



