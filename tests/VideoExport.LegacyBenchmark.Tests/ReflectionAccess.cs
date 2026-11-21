using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
namespace AAVideoExport.LegacyBenchmark.Tests;

// No AAVE assembly reference: only the stable original normal-export entrypoints.
internal static class R
{
 internal const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
 private static readonly Dictionary<(Type,string),MemberInfo?> Members=new();
 internal static object? Get(object owner,string name,bool optional=false)
 {
  Type type=owner as Type??owner.GetType(); var key=(type,name);
  if(!Members.TryGetValue(key,out var member)){member=(MemberInfo?)type.GetProperty(name,Flags)??type.GetField(name,Flags);Members[key]=member;}
  if(member==null){if(optional)return null;throw new MissingMemberException(type.FullName,name);}
  return member is PropertyInfo p?p.GetValue(owner is Type?null:owner):((FieldInfo)member).GetValue(owner is Type?null:owner);
 }
 internal static void Set(object owner,string name,object? value)
 {
  Type type=owner as Type??owner.GetType();var property=type.GetProperty(name,Flags);
  if(property!=null)property.SetValue(owner is Type?null:owner,value);
  else (type.GetField(name,Flags)??throw new MissingFieldException(type.FullName,name)).SetValue(owner is Type?null:owner,value);
 }
 internal static MethodInfo Method(Type type,string name,int arguments)=>type.GetMethods(Flags).Single(m=>m.Name==name&&m.GetParameters().Length==arguments);
 internal static object? Call(object owner,string method,params object?[] args)=>Method(owner as Type??owner.GetType(),method,args.Length).Invoke(owner is Type?null:owner,args);
 internal static bool Bool(object owner,string name,bool optional=false)=>Get(owner,name,optional) is true;
 internal static long Long(object owner,string name)=>Convert.ToInt64(Get(owner,name));
 internal static string Text(object owner,string name,bool optional=false)=>Get(owner,name,optional)?.ToString()??"";
 internal static string Hash(string path){using var f=File.OpenRead(path);using var sha=SHA256.Create();return Convert.ToHexString(sha.ComputeHash(f));}
 internal static Dictionary<string,object?> Scalars(object value)=>value.GetType().GetProperties(Flags).Where(p=>p.GetMethod?.IsPublic==true&&p.GetIndexParameters().Length==0&& (p.PropertyType.IsPrimitive||p.PropertyType==typeof(string)||p.PropertyType==typeof(TimeSpan))).OrderBy(p=>p.Name,StringComparer.Ordinal).ToDictionary(p=>p.Name,p=>p.GetValue(value));
 internal static string Json(object value)=>JsonSerializer.Serialize(value,new JsonSerializerOptions{WriteIndented=true});
}
