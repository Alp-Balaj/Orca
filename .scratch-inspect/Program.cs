using System.Reflection;
var asm = Assembly.LoadFrom(args[0]);
foreach (var name in new[] {"CommandParseException", "CommandRuntimeException", "CommandConfigurationException", "CommandAppException"})
{
    var t = asm.GetType("Spectre.Console.Cli." + name);
    if (t is null) { Console.WriteLine(name + " NOT FOUND"); continue; }
    Console.WriteLine("== " + t.FullName + " (base: " + t.BaseType?.FullName + ") ==");
    foreach (var ctor in t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
    {
        Console.WriteLine("  public=" + ctor.IsPublic + " " + ctor);
    }
}
