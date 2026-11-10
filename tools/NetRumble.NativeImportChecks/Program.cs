using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: NetRumble.NativeImportChecks <MonoGame.Framework.dll> <mgruntime.dll>");
    return 1;
}

using var managedFile = File.OpenRead(args[0]);
using var managed = new PEReader(managedFile);
var metadata = managed.GetMetadataReader();
var imports = new SortedSet<string>(StringComparer.Ordinal);
foreach (var handle in metadata.MethodDefinitions)
{
    var method = metadata.GetMethodDefinition(handle);
    if (!method.Attributes.HasFlag(MethodAttributes.PinvokeImpl)) { continue; }
    var import = method.GetImport();
    var module = metadata.GetString(metadata.GetModuleReference(import.Module).Name);
    if (!module.Equals("mgruntime", StringComparison.OrdinalIgnoreCase)
        && !module.Equals("mgruntime.dll", StringComparison.OrdinalIgnoreCase))
    {
        continue;
    }
    imports.Add(metadata.GetString(import.Name));
}
if (imports.Count == 0)
{
    Console.Error.WriteLine($"FAIL: No mgruntime imports found in '{args[0]}'.");
    return 1;
}

using var nativeFile = File.OpenRead(args[1]);
using var native = new PEReader(nativeFile);
var directory = native.PEHeaders.PEHeader?.ExportTableDirectory
    ?? throw new BadImageFormatException("The native runtime has no PE optional header.");
if (directory.RelativeVirtualAddress == 0)
{
    Console.Error.WriteLine($"FAIL: No export table found in '{args[1]}'.");
    return 1;
}

// IMAGE_EXPORT_DIRECTORY: NumberOfNames is at byte 24; AddressOfNames at byte 32.
var table = native.GetSectionData(directory.RelativeVirtualAddress).GetReader();
table.Offset = 24;
var nameCount = table.ReadUInt32();
table.ReadUInt32();
var namesRva = table.ReadInt32();
var names = native.GetSectionData(namesRva).GetReader();
var exports = new HashSet<string>(StringComparer.Ordinal);
for (uint index = 0; index < nameCount; index++)
{
    var nameRva = names.ReadInt32();
    var bytes = native.GetSectionData(nameRva).GetContent().AsSpan();
    var terminator = bytes.IndexOf((byte)0);
    if (terminator < 0) { throw new BadImageFormatException("Unterminated native export name."); }
    exports.Add(Encoding.ASCII.GetString(bytes[..terminator]));
}

var missing = imports.Where(name => !exports.Contains(name)).ToArray();
if (missing.Length != 0)
{
    Console.Error.WriteLine($"FAIL: '{args[1]}' is missing {missing.Length} entry point(s) required by '{args[0]}':");
    foreach (var name in missing) { Console.Error.WriteLine($"  {name}"); }
    Console.Error.WriteLine("Use matching MonoGame managed sources and Xbox overlay; see docs\\xbox-console-build.md.");
    return 1;
}

Console.WriteLine($"PASS: All {imports.Count} mgruntime imports have exact named exports in '{args[1]}'.");
return 0;
