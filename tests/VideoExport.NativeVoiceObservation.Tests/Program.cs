using System.Reflection;
int passed=0,failed=0;
Type Find(string name)=>Assembly.GetExecutingAssembly().GetType("AAVideoExport.NativePath.Tests."+name)??throw new Exception("Missing source-linked external native voice observer.");
object Position(long frame,long sample,int row)=>Activator.CreateInstance(Find("NativeVoicePosition"),frame,sample,row)!;
object Observer(){var type=Find("NativeVoiceObservedAsset");var asset=Activator.CreateInstance(type,"aavt/voice/fixture",new string('a',64),new string('b',64),4800L,1,48000)!;var assets=Array.CreateInstance(type,1);assets.SetValue(asset,0);return Activator.CreateInstance(Find("NativeVoiceObservation"),30,48000,assets)!;}
object? Call(object owner,string name,params object?[] args)=>owner.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)!.Invoke(owner,args);
void Reject(Action action){try{action();}catch(Exception e)when(e.GetBaseException() is InvalidDataException){return;}throw new Exception("Invalid native voice lifecycle accepted.");}
void Test(string name,Action action){try{action();passed++;Console.WriteLine("PASS "+name);}catch(Exception e){failed++;Console.WriteLine("FAIL "+name+": "+e.GetBaseException().Message);}}
void Request(object o)=>Call(o,"Request",10L,"aavt/voice/fixture",Position(4,6400,2),.3);
Test("external observer records native request start end delay in order",()=>{var o=Observer();Request(o);Call(o,"Start",10L,Position(5,8000,2),1f,1f);Call(o,"End",10L,Position(8,12800,2));Call(o,"DelayComplete",10L,Position(17,27200,2));var result=Call(o,"Complete",Position(20,32000,3))!;var events=(System.Collections.ICollection)result.GetType().GetProperty("Events")!.GetValue(result)!;if(events.Count!=4)throw new Exception("Real lifecycle operations were lost.");});
Test("duplicate start cannot count as a second valid lifecycle event",()=>{var o=Observer();Request(o);Call(o,"Start",10L,Position(5,8000,2),1f,1f);Reject(()=>Call(o,"Start",10L,Position(5,8000,2),1f,1f));});
Test("completed pass rejects a request without native end and delay",()=>{var o=Observer();Request(o);Call(o,"Start",10L,Position(5,8000,2),1f,1f);Reject(()=>Call(o,"Complete",Position(20,32000,3)));});
Test("changed native request sequence is rejected",()=>{var o=Observer();Request(o);Reject(()=>Call(o,"Start",11L,Position(5,8000,2),1f,1f));});
Test("unknown voice asset cannot become accepted evidence",()=>{var o=Observer();Reject(()=>Call(o,"Request",1L,"aavt/voice/other",Position(4,6400,2),.3));});
Test("voice clock reversal is rejected",()=>{var o=Observer();Request(o);Reject(()=>Call(o,"Start",10L,Position(3,4800,2),1f,1f));});
Test("external PCM observer rejects gaps and requires complete authored samples",()=>{var p=Activator.CreateInstance(Find("NativePcmObservation"),30,48000,2)!;Reject(()=>Call(p,"Append",1L,new float[3200],3200));Call(p,"Append",0L,new float[3200],3200);Reject(()=>Call(p,"Complete",2L));});
Test("external PCM compares every authored float independently of unused mixer tail",()=>{
    object Run(float[] samples){var p=Activator.CreateInstance(Find("NativePcmObservation"),30,48000,2)!;Call(p,"Append",0L,samples,samples.Length);return Call(p,"Complete",1L)!;}
    var original=Run(new float[6400]);var changed=new float[6400];changed[25]=.5f;var altered=Run(changed);
    string Hash(object o)=>(string)o.GetType().GetProperty("ContentSha256")!.GetValue(o)!;
    if(Hash(original)==Hash(altered))throw new Exception("Authored PCM change was hidden.");
    var tail=new float[6400];tail[4000]=.5f;if(Hash(original)!=Hash(Run(tail)))throw new Exception("Unused mixer tail changed authored-window comparison.");
});
void CompareSaved(float[] expected, short[] actual, bool reject = false)
{
    string root = Path.Combine(Path.GetTempPath(), "aave-pcm-window-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
    try {
        var observer = Activator.CreateInstance(Find("NativePcmObservation"), 30, 48000, 2)!;
        Call(observer, "Append", 0L, expected, expected.Length);
        object content = Call(observer, "WriteContent", 1L, Path.Combine(root, "original.f32le"))!;
        byte[] bytes = new byte[actual.Length * 2]; Buffer.BlockCopy(actual, 0, bytes, 0, bytes.Length);
        string decoded = Path.Combine(root, "decoded.s16le"); File.WriteAllBytes(decoded, bytes);
        Action run = () => Find("NativePcmObservation").GetMethod("ComparePcm16", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { content, decoded, Path.Combine(root, "comparison.json") });
        if (reject) Reject(run); else run();
    } finally { Directory.Delete(root, true); }
}
Test("actual mixed PCM window agrees with independently decoded PCM16 quantization", () => {
    var native = new float[3200]; var decoded = new short[3200];
    native[13] = .25f; decoded[13] = 8192; native[1417] = -.5f; decoded[1417] = -16384;
    CompareSaved(native, decoded);
});
Test("single-sample shift is rejected despite unchanged frame and sample counts", () => {
    var native = new float[3200]; var decoded = new short[3200]; native[13] = .25f; decoded[15] = 8192;
    CompareSaved(native, decoded, reject: true);
});
Test("decoded silence cannot pass an audible original mixed PCM window", () => {
    var native = new float[3200]; native[13] = .25f; CompareSaved(native, new short[3200], reject: true);
});
Test("partial decoded PCM window cannot claim exact timeline coverage", () => CompareSaved(new float[3200], new short[3198], reject: true));
Console.WriteLine($"RESULT {passed} passed; {failed} failed; external native voice and PCM observations only");return failed==0?0:1;
