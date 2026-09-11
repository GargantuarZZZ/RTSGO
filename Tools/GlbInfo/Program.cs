using System.Numerics;
using System.Text.Json;

if (args.Length == 0)
{
    Console.WriteLine("Usage: GlbInfo <path-to-glb-or-dir>");
    return;
}

var files = new List<string>();
var target = args[0];
if (Directory.Exists(target))
    files.AddRange(Directory.EnumerateFiles(target, "*.glb").OrderBy(f => f));
else if (File.Exists(target))
    files.Add(target);

foreach (var file in files)
{
    Console.WriteLine("==== " + Path.GetFileName(file));
    try
    {
        var glb = ReadGlb(file);
        var doc = glb.Json;
        if (doc.RootElement.TryGetProperty("nodes", out var nodesEl) &&
            doc.RootElement.TryGetProperty("meshes", out var meshesEl) &&
            doc.RootElement.TryGetProperty("accessors", out var accessorsEl))
        {
            var animCount = doc.RootElement.TryGetProperty("animations", out var anims) ? anims.GetArrayLength() : 0;
            if (animCount > 0)
            {
                var names = new List<string>();
                for (int a = 0; a < anims.GetArrayLength(); a++)
                {
                    if (anims[a].TryGetProperty("name", out var an))
                        names.Add(an.GetString() ?? "");
                }
                Console.WriteLine($"  animations({animCount}): {string.Join(", ", names)}");

                if (doc.RootElement.TryGetProperty("animations", out var anims2))
                {
                    for (int a = 0; a < anims2.GetArrayLength(); a++)
                    {
                        var anim = anims2[a];
                        string animName = anim.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
                        if (!animName.Contains("Walk") && !animName.Contains("Run"))
                            continue;
                        if (!anim.TryGetProperty("channels", out var channels))
                            continue;
                        foreach (var ch in channels.EnumerateArray())
                        {
                            if (!ch.TryGetProperty("target", out var tgt) ||
                                !tgt.TryGetProperty("path", out var path) ||
                                path.GetString() != "translation" ||
                                !tgt.TryGetProperty("node", out var nodeRef))
                                continue;
                            int nodeIdx = nodeRef.GetInt32();
                            string nodeName = nodesEl[nodeIdx].TryGetProperty("name", out var nn) ? nn.GetString() ?? "" : "";
                            if (!ch.TryGetProperty("sampler", out var samplerRef))
                                continue;
                            var sampler = anim.GetProperty("samplers")[samplerRef.GetInt32()];
                            var acc = accessorsEl[sampler.GetProperty("output").GetInt32()];
                            if (acc.TryGetProperty("min", out var mn) && acc.TryGetProperty("max", out var mx))
                            {
                                var minV = new Vector3(mn[0].GetSingle(), mn[1].GetSingle(), mn[2].GetSingle());
                                var maxV = new Vector3(mx[0].GetSingle(), mx[1].GetSingle(), mx[2].GetSingle());
                                Console.WriteLine($"    {animName} / {nodeName} trans min=({minV.X:F2},{minV.Y:F2},{minV.Z:F2}) max=({maxV.X:F2},{maxV.Y:F2},{maxV.Z:F2})");
                            }
                        }
                    }
                }
            }

            for (int i = 0; i < nodesEl.GetArrayLength(); i++)
            {
                var node = nodesEl[i];
                if (!node.TryGetProperty("mesh", out var meshRef))
                    continue;
                var name = node.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var mesh = meshesEl[meshRef.GetInt32()];
                var prims = mesh.GetProperty("primitives");
                if (prims.GetArrayLength() == 0)
                    continue;
                var posRef = prims[0].GetProperty("attributes").GetProperty("POSITION").GetInt32();
                var acc = accessorsEl[posRef];
                if (!acc.TryGetProperty("min", out var minEl) || !acc.TryGetProperty("max", out var maxEl))
                    continue;
                var min = new Vector3(minEl[0].GetSingle(), minEl[1].GetSingle(), minEl[2].GetSingle());
                var max = new Vector3(maxEl[0].GetSingle(), maxEl[1].GetSingle(), maxEl[2].GetSingle());
                var scale = node.TryGetProperty("scale", out var sEl)
                    ? new Vector3(sEl[0].GetSingle(), sEl[1].GetSingle(), sEl[2].GetSingle())
                    : Vector3.One;
                var trans = node.TryGetProperty("translation", out var tEl)
                    ? new Vector3(tEl[0].GetSingle(), tEl[1].GetSingle(), tEl[2].GetSingle())
                    : Vector3.Zero;
                var wMin = min * scale + trans;
                var wMax = max * scale + trans;
                var size = wMax - wMin;
                Console.WriteLine(
                    $"  {name}: size=({size.X:F2},{size.Y:F2},{size.Z:F2}) " +
                    $"x=({wMin.X:F2}..{wMax.X:F2}) y=({wMin.Y:F2}..{wMax.Y:F2}) z=({wMin.Z:F2}..{wMax.Z:F2})");
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine("  ERROR: " + ex.Message);
    }
}

static (byte[] Bin, JsonDocument Json) ReadGlb(string path)
{
    var bytes = File.ReadAllBytes(path);
    var jsonLen = BitConverter.ToUInt32(bytes, 12);
    var jsonType = BitConverter.ToUInt32(bytes, 16);
    if (jsonType != 0x4E4F534A)
        throw new InvalidDataException("Not a GLB with JSON chunk.");
    var jsonBytes = bytes.AsSpan(20, (int)jsonLen).ToArray();
    var binLen = 0u;
    if (20 + jsonLen + 8 <= bytes.Length)
        binLen = BitConverter.ToUInt32(bytes, (int)(20 + jsonLen));
    var bin = binLen > 0 ? bytes.AsSpan((int)(28 + jsonLen), (int)binLen).ToArray() : Array.Empty<byte>();
    return (bin, JsonDocument.Parse(jsonBytes));
}
