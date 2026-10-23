using System.Reflection;
using System.Windows.Controls;

public class SettingsProxy : DispatchProxy {
    public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
}

static class SettingsCoverage {
    public static void Run(Assembly extension) {
        var settings_type = extension.GetType("dnSpy.CallGraph.CallGraphSettings", true)!;
        var constructor = settings_type.GetConstructors().Single(); var service_type = constructor.GetParameters()[0].ParameterType;
        var section_type = service_type.GetMethod("GetOrCreateSection")!.ReturnType;
        var attributes = new Dictionary<string, object?>();
        object Proxy(Type type, Func<MethodInfo, object?[]?, object?> handler) { var proxy = DispatchProxy.Create(type, typeof(SettingsProxy)); ((SettingsProxy)proxy).Handler = handler; return proxy; }
        var section = Proxy(section_type, (method, args) => {
            if (method.Name == "Attribute") {
                string key = (string)args![0]!;
                if (args.Length == 2) { attributes[key] = args[1]; return null; }
                return attributes.TryGetValue(key, out var value) ? value : null;
            }
            throw new NotSupportedException(method.Name);
        });
        var service = Proxy(service_type, (method, _) => {
            if (method.Name == "RecreateSection") attributes.Clear();
            if (method.Name is "GetOrCreateSection" or "RecreateSection") return section;
            throw new NotSupportedException(method.Name);
        });
        object Settings() => constructor.Invoke(new[] { service });
        var settings = Settings(); var property = settings_type.GetProperty("OnlyCurrentAssembly")!;
        if ((bool)property.GetValue(settings)!) throw new Exception("Assembly filter must default to off for existing users");
        var page_type = extension.GetType("dnSpy.CallGraph.CallGraphSettingsPage", true)!;
        var page = Activator.CreateInstance(page_type, settings)!;
        var check = (CheckBox)page_type.GetField("only_current_assembly_check_box", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        check.IsChecked = true; page_type.GetMethod("OnApply")!.Invoke(page, null);
        if (!(bool)property.GetValue(settings)! || !(bool)property.GetValue(Settings())!) throw new Exception("Assembly filter was not applied/persisted");
        var filters = settings_type.GetMethod("CreateFilterOptions")!.Invoke(settings, null)!;
        if (!(bool)filters.GetType().GetProperty("OnlyCurrentAssembly")!.GetValue(filters)!) throw new Exception("Settings do not reach the graph filter");
        check.IsChecked = false; page_type.GetMethod("OnApply")!.Invoke(page, null);
        if ((bool)property.GetValue(Settings())!) throw new Exception("Assembly filter cannot be switched back off");
        Console.WriteLine("PASS settings assembly checkbox default/apply/persistence/filter/off");
    }
}
