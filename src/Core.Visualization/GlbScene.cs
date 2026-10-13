using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;

namespace Core.Visualization;

public sealed record GlbPart(string Name, Face3D[] Faces, Matrix4x4 World);
public sealed class GlbScene
{
    public IReadOnlyList<GlbPart> Parts { get; }
    private GlbScene(List<GlbPart> parts) => Parts = parts;
    public static GlbScene Load(Stream stream)
    {
        using var memory = new MemoryStream(); stream.CopyTo(memory); var data = memory.ToArray();
        if (data.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x46546c67 || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)) != 2 || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8)) != data.Length)
            throw new InvalidDataException("GLB 2.0 헤더가 올바르지 않음");
        var offset = 12; JsonDocument? json = null; ReadOnlyMemory<byte> bin = default;
        while (offset + 8 <= data.Length)
        {
            var size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset))); var kind = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4)); offset += 8;
            if (size < 0 || offset + size > data.Length) throw new InvalidDataException("GLB 청크 길이 오류");
            if (kind == 0x4e4f534a) json = JsonDocument.Parse(data.AsMemory(offset, size));
            else if (kind == 0x004e4942) bin = data.AsMemory(offset, size);
            offset += size;
        }
        if (json is null || bin.IsEmpty) throw new InvalidDataException("GLB JSON/BIN 청크 누락");
        using (json)
        {
            var root = json.RootElement;
            if (root.TryGetProperty("extensionsRequired", out var required) && required.EnumerateArray().Any(e => e.GetString() != "KHR_lights_punctual")) throw new NotSupportedException("필수 GLB 확장 미지원");
            if (root.TryGetProperty("animations", out var animations) && animations.GetArrayLength() > 0) throw new NotSupportedException("GLB 애니메이션 미지원");
            if (root.TryGetProperty("images", out var images) && images.GetArrayLength() > 0) throw new NotSupportedException("GLB 텍스처 미지원");
            if(root.GetProperty("buffers").GetArrayLength()!=1 || root.GetProperty("buffers")[0].TryGetProperty("uri",out _)) throw new NotSupportedException("외부 버퍼 또는 복수 버퍼 미지원");
            var nodes = root.GetProperty("nodes"); var meshes = root.GetProperty("meshes"); var scenes = root.GetProperty("scenes");
            var sceneIndex = root.TryGetProperty("scene", out var si) ? si.GetInt32() : 0;
            var parts = new List<GlbPart>();
            foreach (var node in scenes[sceneIndex].GetProperty("nodes").EnumerateArray()) Visit(node.GetInt32(), Matrix4x4.Identity);
            return new GlbScene(parts);
            void Visit(int index, Matrix4x4 parent)
            {
                var node = nodes[index]; var local = Transform(node); var world = local * parent;
                if (!Matrix4x4.Invert(world, out var inverse)) throw new InvalidDataException("GLB 노드 변환 역행렬 없음");
                var normalTransform = Matrix4x4.Transpose(inverse);
                if (node.TryGetProperty("mesh", out var meshId))
                {
                    var name = node.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var faces = new List<Face3D>();
                    foreach (var primitive in meshes[meshId.GetInt32()].GetProperty("primitives").EnumerateArray())
                    {
                        if (primitive.TryGetProperty("mode", out var mode) && mode.GetInt32() != 4) throw new NotSupportedException("TRIANGLES 외 primitive 미지원");
                        if (!primitive.TryGetProperty("indices", out var indices)) throw new NotSupportedException("인덱스 없는 primitive 미지원");
                        var attrs = primitive.GetProperty("attributes");
                        var positions = ReadVectors(attrs.GetProperty("POSITION").GetInt32());
                        var normals = ReadVectors(attrs.GetProperty("NORMAL").GetInt32());
                        if (normals.Length != positions.Length) throw new InvalidDataException("POSITION/NORMAL 개수 불일치");
                        var ids = ReadIndices(indices.GetInt32());
                        var color = new Rgb(160, 175, 185); var metallic = 1f; var roughness = 1f;
                        if (primitive.TryGetProperty("material", out var materialId))
                        {
                            var material = root.GetProperty("materials")[materialId.GetInt32()];
                            if (material.TryGetProperty("pbrMetallicRoughness", out var pbr))
                            {
                                if (pbr.TryGetProperty("baseColorFactor", out var factor)) color = new Rgb(Channel(factor[0]), Channel(factor[1]), Channel(factor[2]));
                                if (pbr.TryGetProperty("metallicFactor", out var mf)) metallic = mf.GetSingle();
                                if (pbr.TryGetProperty("roughnessFactor", out var rf)) roughness = rf.GetSingle();
                            }
                        }
                        if (ids.Length % 3 != 0) throw new InvalidDataException("삼각형 인덱스 길이 오류");
                        for (var i = 0; i < ids.Length; i += 3)
                        {
                            if (ids[i] >= positions.Length || ids[i+1] >= positions.Length || ids[i+2] >= positions.Length) throw new InvalidDataException("인덱스 범위 오류");
                            faces.Add(new Face3D([Vector3.Transform(positions[ids[i]], world),Vector3.Transform(positions[ids[i+1]], world),Vector3.Transform(positions[ids[i+2]], world)],color,name)
                            { Normals = [Vector3.Normalize(Vector3.TransformNormal(normals[ids[i]], normalTransform)),Vector3.Normalize(Vector3.TransformNormal(normals[ids[i+1]], normalTransform)),Vector3.Normalize(Vector3.TransformNormal(normals[ids[i+2]], normalTransform))], Metallic = metallic, Roughness = roughness, LinearColor = true });
                        }
                    }
                    parts.Add(new GlbPart(name, faces.ToArray(), world));
                }
                if (node.TryGetProperty("children", out var children)) foreach (var child in children.EnumerateArray()) Visit(child.GetInt32(), world);
            }
            Vector3[] ReadVectors(int accessorId)
            {
                var a = root.GetProperty("accessors")[accessorId];
                if (a.GetProperty("type").GetString() != "VEC3" || a.GetProperty("componentType").GetInt32() != 5126 || a.TryGetProperty("sparse", out _)) throw new NotSupportedException("POSITION/NORMAL은 float VEC3만 지원");
                var view = root.GetProperty("bufferViews")[a.GetProperty("bufferView").GetInt32()];
                var stride = view.TryGetProperty("byteStride", out var st) ? st.GetInt32() : 12;
                if (stride < 12) throw new InvalidDataException("버퍼 stride 오류");
                var start = Offset(view) + (a.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0);
                var count = a.GetProperty("count").GetInt32(); var result = new Vector3[count];
                for (var i = 0; i < count; i++) { var at = start + i * stride; if (at < 0 || at + 12 > bin.Length) throw new InvalidDataException("정점 버퍼 범위 오류"); var span = bin.Span.Slice(at,12); result[i] = new Vector3(BitConverter.ToSingle(span),BitConverter.ToSingle(span[4..]),BitConverter.ToSingle(span[8..])); }
                return result;
            }
            int[] ReadIndices(int accessorId)
            {
                var a = root.GetProperty("accessors")[accessorId]; var view = root.GetProperty("bufferViews")[a.GetProperty("bufferView").GetInt32()];
                var type = a.GetProperty("componentType").GetInt32(); var size = type switch { 5121 => 1, 5123 => 2, 5125 => 4, _ => throw new NotSupportedException("인덱스 타입 미지원") };
                var stride = view.TryGetProperty("byteStride", out var st) ? st.GetInt32() : size;
                var start = Offset(view) + (a.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0);
                var count = a.GetProperty("count").GetInt32(); var result = new int[count];
                for (var i = 0; i < count; i++) { var at = start + i * stride; if (at < 0 || at + size > bin.Length) throw new InvalidDataException("인덱스 버퍼 범위 오류"); var span = bin.Span.Slice(at,size); result[i] = type switch { 5121 => span[0], 5123 => BinaryPrimitives.ReadUInt16LittleEndian(span), _ => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(span)) }; }
                return result;
            }
            int Offset(JsonElement view)
            {
                if (view.GetProperty("buffer").GetInt32() != 0) throw new NotSupportedException("내장 BIN 하나만 지원");
                return view.TryGetProperty("byteOffset", out var value) ? value.GetInt32() : 0;
            }
        }
    }
    private static byte Channel(JsonElement value) => (byte)Math.Clamp((int)MathF.Round(value.GetSingle()*255),0,255);
    private static Matrix4x4 Transform(JsonElement node)
    {
        if (node.TryGetProperty("matrix", out var m)) return new Matrix4x4(m[0].GetSingle(),m[1].GetSingle(),m[2].GetSingle(),m[3].GetSingle(),m[4].GetSingle(),m[5].GetSingle(),m[6].GetSingle(),m[7].GetSingle(),m[8].GetSingle(),m[9].GetSingle(),m[10].GetSingle(),m[11].GetSingle(),m[12].GetSingle(),m[13].GetSingle(),m[14].GetSingle(),m[15].GetSingle());
        var t = node.TryGetProperty("translation",out var translation) ? new Vector3(translation[0].GetSingle(),translation[1].GetSingle(),translation[2].GetSingle()) : Vector3.Zero;
        var s = node.TryGetProperty("scale",out var scale) ? new Vector3(scale[0].GetSingle(),scale[1].GetSingle(),scale[2].GetSingle()) : Vector3.One;
        var q = node.TryGetProperty("rotation",out var rotation) ? new Quaternion(rotation[0].GetSingle(),rotation[1].GetSingle(),rotation[2].GetSingle(),rotation[3].GetSingle()) : Quaternion.Identity;
        return Matrix4x4.CreateScale(s)*Matrix4x4.CreateFromQuaternion(q)*Matrix4x4.CreateTranslation(t);
    }
}
