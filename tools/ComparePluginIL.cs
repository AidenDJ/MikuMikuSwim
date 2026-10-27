using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

public static class ComparePluginIL
{
    private static void Collect(TypeDefinition type,Dictionary<string,string> entries)
    {
        entries.Add("T "+type.FullName,type.BaseType==null?"":type.BaseType.FullName);
        foreach(FieldDefinition field in type.Fields)
            entries.Add("F "+type.FullName+"::"+field.Name,field.FieldType.FullName+"|"+field.Attributes+"|"+field.Constant
                +"|"+Convert.ToBase64String(field.InitialValue ?? new byte[0]));
        foreach(MethodDefinition method in type.Methods)
        {
            string value=method.Attributes+"|"+method.ImplAttributes;
            if(method.HasBody)
            {
                value+="|"+method.Body.InitLocals+"|"+string.Join(",",method.Body.Variables.Select(v=>v.VariableType.FullName).ToArray());
                value+="\n"+string.Join("\n",method.Body.Instructions.Select(i=>i.ToString()).ToArray());
                foreach(var handler in method.Body.ExceptionHandlers)
                    value+="\nEH "+handler.HandlerType+"|"+handler.TryStart+"|"+handler.TryEnd+"|"+handler.HandlerStart+"|"+handler.HandlerEnd+"|"+handler.FilterStart+"|"+handler.CatchType;
            }
            entries.Add("M "+method.FullName,value);
        }
        foreach(TypeDefinition nested in type.NestedTypes) Collect(nested,entries);
    }
    private static Dictionary<string,string> Read(string path)
    {
        using(AssemblyDefinition assembly=AssemblyDefinition.ReadAssembly(path))
        {
            Dictionary<string,string> entries=new Dictionary<string,string>();
            foreach(TypeDefinition type in assembly.MainModule.Types) Collect(type,entries);
            TypeDefinition plugin=assembly.MainModule.Types.First(t=>t.FullName=="MikuPlayerReplacer.MikuPlayerReplacerPlugin");
            string version=(string)plugin.Fields.First(f=>f.Name=="PluginVersion").Constant;
            if(version!="0.1.19") throw new Exception("Unexpected version: "+version);
            var attribute=plugin.CustomAttributes.First(a=>a.AttributeType.FullName=="BepInEx.BepInPlugin");
            if((string)attribute.ConstructorArguments[2].Value!="0.1.19") throw new Exception("Plugin metadata version mismatch");
            return entries;
        }
    }
    public static void Main(string[] args)
    {
        var original=Read(args[0]); var rebuilt=Read(args[1]);
        var keys=new HashSet<string>(original.Keys); keys.UnionWith(rebuilt.Keys);
        List<string> changed=new List<string>();
        foreach(string key in keys)
        { string a,b; if(!original.TryGetValue(key,out a) || !rebuilt.TryGetValue(key,out b) || a!=b) changed.Add(key); }
        Console.WriteLine("Reference definitions: "+original.Count+"; rebuilt definitions: "+rebuilt.Count);
        foreach(string key in changed.Take(12)) Console.WriteLine("DIFF "+key);
        Console.WriteLine("Changed definitions: "+changed.Count);
        if(changed.Count!=0) Environment.ExitCode=1;
        else Console.WriteLine("0.1.19 executable code and plugin version match the reference.");
    }
}
