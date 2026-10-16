// Copyright (C) 2026 Revive101. AGPL-3.0-or-later.
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

try {
    var folder = Path.GetFullPath(args[0]);
    AssemblyLoadContext.Default.Resolving += (_, name) => {
        var file = Path.Combine(folder, name.Name + ".dll");
        return File.Exists(file) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(file) : null;
    };
    var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(folder, "Imcodec.ObjectProperty.dll"));
    Type TypeOf(string name) => assembly.GetType("Imcodec.ObjectProperty.TypeCache." + name, true)!;
    object New(string name) => Activator.CreateInstance(TypeOf(name))!;
    void Set(object instance, string name, object value) {
        var p = instance.GetType().GetProperty(name)!; var target = p.PropertyType;
        if (target.IsEnum) { value = Enum.ToObject(target, value); }
        else if (!target.IsInstanceOfType(value)) {
            var convert = target.GetMethods(BindingFlags.Static | BindingFlags.Public).FirstOrDefault(m => (m.Name is "op_Implicit" or "op_Explicit") && m.ReturnType == target && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == value.GetType());
            if (convert is null && target.Namespace?.Contains(".Bit") == true) {
                value = Convert.ToByte(value);
                convert = target.GetMethods(BindingFlags.Static | BindingFlags.Public).First(m => (m.Name is "op_Implicit" or "op_Explicit") && m.ReturnType == target && m.GetParameters()[0].ParameterType == typeof(byte));
            }
            value = convert is null ? Convert.ChangeType(value, target) : convert.Invoke(null, [value])!;
        }
        p.SetValue(instance, value);
    }
    object Get(object instance, string name) => instance.GetType().GetProperty(name)!.GetValue(instance)!;
    Dictionary<string,string> Snapshot(object info) {
        var values = new Dictionary<string,string>();
        foreach (var name in new[] { "m_templateID", "m_name", "m_globalID", "m_userID", "m_location", "m_level", "m_world", "m_schoolOfFocus", "m_nameIndices" }) { values[name] = Get(info,name).ToString()!; }
        var avatar = Get(info,"m_avatarBehavior");
        foreach (var name in new[] { "m_nHairModel", "m_nHatColor", "m_nTorsoColor", "m_nFeetColor", "m_eGender", "m_newPlayerOptions", "m_afterCombatVictoryDance" }) { values[name] = Get(avatar,name).ToString()!; }
        return values;
    }
    var serializerType = assembly.GetType("Imcodec.ObjectProperty.ObjectSerializer", true)!;
    var flagsType = assembly.GetType("Imcodec.ObjectProperty.SerializerFlags", true)!;
    object Serializer() => Activator.CreateInstance(serializerType, [false, Enum.ToObject(flagsType, 0), null, true])!;
    var encode = serializerType.GetMethods().Single(m => m.Name == "Serialize" && m.GetParameters().Length == 3 && m.GetParameters()[1].ParameterType.IsEnum);
    var decode = serializerType.GetMethods().Single(m => m.Name == "Deserialize" && m.IsGenericMethodDefinition && m.GetParameters()[1].ParameterType.IsEnum).MakeGenericMethod(TypeOf("WizardCharacterCreationInfo"));
    var propertyMask = Enum.ToObject(encode.GetParameters()[1].ParameterType, 24);
    if (args[1] == "api") {
        var api = assembly.GetExportedTypes().OrderBy(t=>t.FullName).Select(t=>new { Name=t.FullName, Base=t.BaseType?.FullName, Members=t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Select(m=>m.ToString()).OrderBy(m=>m).ToArray() }).ToArray();
        File.WriteAllText(args[2],JsonSerializer.Serialize(api)); return 0;
    }
    if (args[1] == "write") {
        var fixtures = new List<object>();
        for (var gender = 0; gender < 2; gender++) {
            for (var school = 0; school < 7; school++) {
                var info = New("WizardCharacterCreationInfo"); var avatar = New("WizardCharacterBehavior");
                Set(info,"m_templateID",7); Set(info,"m_name","Protocol Fixture"); Set(info,"m_globalID",123456UL); Set(info,"m_userID",654321UL);
                Set(info,"m_location","WizardCity/WC_Hub"); Set(info,"m_level",1); Set(info,"m_world",2); Set(info,"m_schoolOfFocus",(uint)school); Set(info,"m_nameIndices",0x00102030U);
                Set(info,"m_avatarBehavior",avatar); Set(info,"m_equipmentInfoList",New("EquippedItemInfoList"));
                Set(avatar,"m_nHairModel",3); Set(avatar,"m_nHatColor",9); Set(avatar,"m_nTorsoColor",10); Set(avatar,"m_nFeetColor",11); Set(avatar,"m_eGender",gender);
                Set(avatar,"m_newPlayerOptions",0x10203U); Set(avatar,"m_afterCombatVictoryDance",17U);
                object?[] input = [info,propertyMask,null];
                if (!(bool)encode.Invoke(Serializer(),input)!) { throw new Exception("Reference encode failed"); }
                var bytesObject = input[2]!;
                var bytes = (byte[])bytesObject.GetType().GetMethods(BindingFlags.Public | BindingFlags.Static).Single(m => m.Name == "op_Implicit" && m.ReturnType == typeof(byte[])).Invoke(null,[bytesObject])!;
                fixtures.Add(new { Data=Convert.ToBase64String(bytes), Expected=Snapshot(info) });
            }
        }
        File.WriteAllText(args[2],JsonSerializer.Serialize(fixtures));
        Console.WriteLine("Created 14 independent reference character-creation fixtures (all schools and both genders).");
    } else {
        using var fixtures = JsonDocument.Parse(File.ReadAllText(args[2]));
        foreach (var fixture in fixtures.RootElement.EnumerateArray()) {
            object?[] input = [Convert.FromBase64String(fixture.GetProperty("Data").GetString()!),propertyMask,null];
            if (!(bool)decode.Invoke(Serializer(),input)!) { throw new Exception("Server decode failed"); }
            var actual = Snapshot(input[2]!);
            foreach (var expected in fixture.GetProperty("Expected").EnumerateObject()) {
                if (actual[expected.Name] != expected.Value.GetString()) { throw new Exception("Incorrect decoded field: "+expected.Name); }
            }
        }
        Console.WriteLine("PASS: All 14 reference client-order messages decoded with names, schools, genders, IDs and appearance intact.");
    }
    return 0;
} catch(Exception exception) { Console.Error.WriteLine(exception.InnerException ?? exception); return 1; }
