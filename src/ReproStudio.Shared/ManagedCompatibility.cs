using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace ReproStudio.Shared;

/// <summary>Metadata-only checks; provisioning must never initialize WinRT or load the SDK.</summary>
internal static class ManagedCompatibility
{
    public static bool TryIdentity(string path, out AssemblyName? identity)
    {
        try { identity = AssemblyName.GetAssemblyName(path); return true; }
        catch (BadImageFormatException) { identity = null; return false; }
    }

    public static bool CanSatisfy(AssemblyName actual, AssemblyName required) =>
        string.Equals(actual.Name, required.Name, StringComparison.OrdinalIgnoreCase)
        && string.Equals(actual.CultureName ?? "", required.CultureName ?? "", StringComparison.OrdinalIgnoreCase)
        && (actual.Version ?? new Version()) >= (required.Version ?? new Version())
        && (actual.GetPublicKeyToken() ?? []).SequenceEqual(required.GetPublicKeyToken() ?? []);

    public static void Validate(string directory, IEnumerable<string> sdkFiles)
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var identities = new Dictionary<string, AssemblyName>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateFiles(directory, "*.dll"))
        {
            if (!TryIdentity(path, out var identity)) continue;
            if (!paths.TryAdd(identity!.Name!, path)) throw new InvalidOperationException("Duplicate managed identity: " + identity.Name);
            identities.Add(identity.Name!, identity);
        }
        string[] requiredFiles = sdkFiles.Concat(new[] { "ReproStudio.Runner.dll", "ReproStudio.Shared.dll" })
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (string file in requiredFiles)
        {
            using var image = new Image(Path.Combine(directory, file));
            foreach (var handle in image.Reader.AssemblyReferences)
            {
                AssemblyName required = image.Reader.GetAssemblyReference(handle).GetAssemblyName();
                if (!identities.TryGetValue(required.Name!, out var actual) || !CanSatisfy(actual, required))
                    throw Incompatible($"{file} needs {required.FullName}; supplied {actual?.FullName ?? "nothing"}.");
            }
        }
        // Assembly version 3.0 alone is not a baseline compatibility guarantee.
        // Validate actual SDK type/member references baked into both app assemblies.
        var sdkNames = sdkFiles.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var images = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        Image Open(string name)
        {
            if (!images.TryGetValue(name, out var image))
                images.Add(name, image = new Image(paths[name]));
            return image;
        }
        try
        {
            foreach (string app in new[] { "ReproStudio.Runner", "ReproStudio.Shared" })
            {
                Image source = Open(app);
                foreach (TypeReferenceHandle handle in source.Reader.TypeReferences)
                {
                    string? assembly = ScopeAssembly(source.Reader, handle);
                    if (assembly is null || !sdkNames.Contains(assembly)) continue;
                    string name = TypeName(source.Reader, handle);
                    if (!Open(assembly).Types.ContainsKey(name))
                        throw Incompatible($"{app} requires type {assembly}:{name}.");
                }
                foreach (MemberReferenceHandle handle in source.Reader.MemberReferences)
                {
                    var member = source.Reader.GetMemberReference(handle);
                    if (member.Parent.Kind != HandleKind.TypeReference) continue;
                    var type = (TypeReferenceHandle)member.Parent;
                    string? assembly = ScopeAssembly(source.Reader, type);
                    if (assembly is null || !sdkNames.Contains(assembly)) continue;
                    string typeName = TypeName(source.Reader, type);
                    string name = source.Reader.GetString(member.Name);
                    string signature = member.GetKind() == MemberReferenceKind.Method
                        ? Signature(member.DecodeMethodSignature(Signatures.Instance, null))
                        : member.DecodeFieldSignature(Signatures.Instance, null);
                    if (!HasMember(Open(assembly), typeName, name, signature, member.GetKind(), Open, 0))
                        throw Incompatible($"{app} requires {assembly}:{typeName}.{name} {signature}.");
                }
            }
        }
        finally { foreach (Image image in images.Values) image.Dispose(); }
    }

    private static bool HasMember(Image image, string typeName, string name, string signature,
        MemberReferenceKind kind, Func<string, Image> open, int depth)
    {
        if (depth > 32 || !image.Types.TryGetValue(typeName, out var handle)) return false;
        TypeDefinition type = image.Reader.GetTypeDefinition(handle);
        if (kind == MemberReferenceKind.Method)
        {
            foreach (var methodHandle in type.GetMethods())
            {
                var method = image.Reader.GetMethodDefinition(methodHandle);
                if (image.Reader.GetString(method.Name) == name
                    && Signature(method.DecodeSignature(Signatures.Instance, null)) == signature) return true;
            }
        }
        else
        {
            foreach (var fieldHandle in type.GetFields())
            {
                var field = image.Reader.GetFieldDefinition(fieldHandle);
                if (image.Reader.GetString(field.Name) == name
                    && field.DecodeSignature(Signatures.Instance, null) == signature) return true;
            }
        }
        if (name == ".ctor") return false;
        if (type.BaseType.Kind == HandleKind.TypeDefinition)
            return HasMember(image, TypeName(image.Reader, (TypeDefinitionHandle)type.BaseType), name, signature, kind, open, depth + 1);
        if (type.BaseType.Kind == HandleKind.TypeReference)
        {
            var parent = (TypeReferenceHandle)type.BaseType;
            string? assembly = ScopeAssembly(image.Reader, parent);
            if (assembly is not null)
                return HasMember(open(assembly), TypeName(image.Reader, parent), name, signature, kind, open, depth + 1);
        }
        return false;
    }

    private static InvalidOperationException Incompatible(string detail) =>
        new("Selected SDK/API is incompatible with this bundled Runner. " + detail
            + " Choose another SDK or --sdk base (// sdk: base) to retain the bundled APIs.");

    private static string Signature(MethodSignature<string> signature) =>
        $"{signature.Header.IsInstance}:{signature.GenericParameterCount}:{signature.ReturnType}({string.Join(",", signature.ParameterTypes)})";

    private static string? ScopeAssembly(MetadataReader reader, TypeReferenceHandle handle)
    {
        EntityHandle scope = reader.GetTypeReference(handle).ResolutionScope;
        return scope.Kind == HandleKind.AssemblyReference
            ? reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name)
            : scope.Kind == HandleKind.TypeReference ? ScopeAssembly(reader, (TypeReferenceHandle)scope) : null;
    }

    private static string TypeName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var type = reader.GetTypeReference(handle);
        return type.ResolutionScope.Kind == HandleKind.TypeReference
            ? TypeName(reader, (TypeReferenceHandle)type.ResolutionScope) + "+" + reader.GetString(type.Name)
            : Join(reader.GetString(type.Namespace), reader.GetString(type.Name));
    }

    private static string TypeName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        return !type.GetDeclaringType().IsNil
            ? TypeName(reader, type.GetDeclaringType()) + "+" + reader.GetString(type.Name)
            : Join(reader.GetString(type.Namespace), reader.GetString(type.Name));
    }

    private static string Join(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;

    private sealed class Image : IDisposable
    {
        private readonly FileStream _stream;
        private readonly PEReader _pe;
        public MetadataReader Reader { get; }
        public Dictionary<string, TypeDefinitionHandle> Types { get; }
        public Image(string path)
        {
            _stream = File.OpenRead(path);
            _pe = new PEReader(_stream);
            Reader = _pe.GetMetadataReader();
            Types = Reader.TypeDefinitions.ToDictionary(h => TypeName(Reader, h), h => h);
        }
        public void Dispose() { _pe.Dispose(); _stream.Dispose(); }
    }

    private sealed class Signatures : ISignatureTypeProvider<string, object?>
    {
        public static readonly Signatures Instance = new();
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', shape.Rank - 1) + "]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetFunctionPointerType(MethodSignature<string> signature) => "fn:" + Signature(signature);
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType + "<" + string.Join(",", typeArguments) + ">";
        public string GetGenericMethodParameter(object? context, int index) => "!!" + index;
        public string GetGenericTypeParameter(object? context, int index) => "!" + index;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetPinnedType(string elementType) => elementType;
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => TypeName(reader, handle);
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => TypeName(reader, handle);
        public string GetTypeFromSpecification(MetadataReader reader, object? context, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, context);
    }
}
