using System;
using System.Buffers.Binary;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace SrmMetadataBuilderRewrite;

/// <summary>
/// Helper reading rows directly from unmanaged metadata for the 10 structural ECMA-335 tables
/// not exposed as direct collections on <see cref="MetadataReader"/>.
/// </summary>
internal sealed unsafe class RawTableReader
{
    private readonly MetadataReader _reader;
    private readonly byte* _metadataPtr;
    private readonly int _metadataLength;

    private readonly bool _isString4Bytes;
    private readonly bool _isBlob4Bytes;
    private readonly bool _isGuid4Bytes;

    private readonly bool _isTypeDef4Bytes;
    private readonly bool _isTypeRef4Bytes;
    private readonly bool _isTypeSpec4Bytes;
    private readonly bool _isField4Bytes;
    private readonly bool _isMethodDef4Bytes;
    private readonly bool _isMemberRef4Bytes;
    private readonly bool _isParam4Bytes;
    private readonly bool _isEvent4Bytes;
    private readonly bool _isProperty4Bytes;
    private readonly bool _isModuleRef4Bytes;

    private readonly bool _isTypeDefOrRef4Bytes;
    private readonly bool _isHasFieldMarshal4Bytes;
    private readonly bool _isHasSemantics4Bytes;
    private readonly bool _isMethodDefOrRef4Bytes;
    private readonly bool _isMemberForwarded4Bytes;

    public RawTableReader(MetadataReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _metadataPtr = reader.MetadataPointer;
        _metadataLength = reader.MetadataLength;

        _isString4Bytes = reader.GetHeapSize(HeapIndex.String) >= 65536;
        _isBlob4Bytes = reader.GetHeapSize(HeapIndex.Blob) >= 65536;
        _isGuid4Bytes = reader.GetHeapSize(HeapIndex.Guid) >= 65536;

        _isTypeDef4Bytes = reader.GetTableRowCount(TableIndex.TypeDef) >= 65536;
        _isTypeRef4Bytes = reader.GetTableRowCount(TableIndex.TypeRef) >= 65536;
        _isTypeSpec4Bytes = reader.GetTableRowCount(TableIndex.TypeSpec) >= 65536;
        _isField4Bytes = reader.GetTableRowCount(TableIndex.Field) >= 65536;
        _isMethodDef4Bytes = reader.GetTableRowCount(TableIndex.MethodDef) >= 65536;
        _isMemberRef4Bytes = reader.GetTableRowCount(TableIndex.MemberRef) >= 65536;
        _isParam4Bytes = reader.GetTableRowCount(TableIndex.Param) >= 65536;
        _isEvent4Bytes = reader.GetTableRowCount(TableIndex.Event) >= 65536;
        _isProperty4Bytes = reader.GetTableRowCount(TableIndex.Property) >= 65536;
        _isModuleRef4Bytes = reader.GetTableRowCount(TableIndex.ModuleRef) >= 65536;

        // Coded indexes
        _isTypeDefOrRef4Bytes = IsCodedIndex4Bytes([TableIndex.TypeDef, TableIndex.TypeRef, TableIndex.TypeSpec], 2);
        _isHasFieldMarshal4Bytes = IsCodedIndex4Bytes([TableIndex.Field, TableIndex.Param], 1);
        _isHasSemantics4Bytes = IsCodedIndex4Bytes([TableIndex.Event, TableIndex.Property], 1);
        _isMethodDefOrRef4Bytes = IsCodedIndex4Bytes([TableIndex.MethodDef, TableIndex.MemberRef], 1);
        _isMemberForwarded4Bytes = IsCodedIndex4Bytes([TableIndex.Field, TableIndex.MethodDef], 1);
    }

    private bool IsCodedIndex4Bytes(TableIndex[] tables, int tagBits)
    {
        int maxRows = tables.Max(t => _reader.GetTableRowCount(t));
        return maxRows >= (1 << (16 - tagBits));
    }

    private int StringIndexSize => _isString4Bytes ? 4 : 2;
    private int BlobIndexSize => _isBlob4Bytes ? 4 : 2;

    private int ReadIndex(byte* ptr, bool is4Bytes) =>
        is4Bytes ? BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(ptr, 4))
                 : BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(ptr, 2));

    private uint ReadUInt(byte* ptr, bool is4Bytes) =>
        is4Bytes ? BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(ptr, 4))
                 : BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(ptr, 2));

    private byte* GetRowPtr(TableIndex table, int rowId)
    {
        int tableOffset = _reader.GetTableMetadataOffset(table);
        int rowSize = _reader.GetTableRowSize(table);
        return _metadataPtr + tableOffset + (rowId - 1) * rowSize;
    }

    public void ReadTypeDefLists(int rowId, out int fieldListIndex, out int methodListIndex)
    {
        byte* ptr = GetRowPtr(TableIndex.TypeDef, rowId);
        int col = 4 + StringIndexSize * 2 + (_isTypeDefOrRef4Bytes ? 4 : 2);
        fieldListIndex = ReadIndex(ptr + col, _isField4Bytes);
        col += _isField4Bytes ? 4 : 2;
        methodListIndex = ReadIndex(ptr + col, _isMethodDef4Bytes);
    }

    public void ReadMethodDefParamList(int rowId, out int paramListIndex)
    {
        byte* ptr = GetRowPtr(TableIndex.MethodDef, rowId);
        int col = 8 + StringIndexSize + BlobIndexSize;
        paramListIndex = ReadIndex(ptr + col, _isParam4Bytes);
    }

    public void ReadInterfaceImpl(int rowId, out TypeDefinitionHandle classType, out EntityHandle implementedInterface)
    {
        byte* ptr = GetRowPtr(TableIndex.InterfaceImpl, rowId);
        int classRow = ReadIndex(ptr, _isTypeDef4Bytes);
        int col = _isTypeDef4Bytes ? 4 : 2;
        uint rawIface = ReadUInt(ptr + col, _isTypeDefOrRef4Bytes);
        int tag = (int)(rawIface & 3);
        int ifaceRow = (int)(rawIface >> 2);
        implementedInterface = tag switch
        {
            0 => MetadataTokens.TypeDefinitionHandle(ifaceRow),
            1 => MetadataTokens.TypeReferenceHandle(ifaceRow),
            _ => MetadataTokens.TypeSpecificationHandle(ifaceRow)
        };
        classType = MetadataTokens.TypeDefinitionHandle(classRow);
    }

    public void ReadFieldMarshal(int rowId, out EntityHandle parent, out BlobHandle nativeType)
    {
        byte* ptr = GetRowPtr(TableIndex.FieldMarshal, rowId);
        uint rawParent = ReadUInt(ptr, _isHasFieldMarshal4Bytes);
        int tag = (int)(rawParent & 1);
        int parentRow = (int)(rawParent >> 1);
        parent = (tag == 0) ? MetadataTokens.FieldDefinitionHandle(parentRow) : MetadataTokens.ParameterHandle(parentRow);

        int col = _isHasFieldMarshal4Bytes ? 4 : 2;
        int blobOffset = ReadIndex(ptr + col, _isBlob4Bytes);
        nativeType = MetadataTokens.BlobHandle(blobOffset);
    }

    public void ReadClassLayout(int rowId, out ushort packingSize, out uint classSize, out TypeDefinitionHandle parent)
    {
        byte* ptr = GetRowPtr(TableIndex.ClassLayout, rowId);
        packingSize = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(ptr, 2));
        classSize = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(ptr + 2, 4));
        int typeDefRow = ReadIndex(ptr + 6, _isTypeDef4Bytes);
        parent = MetadataTokens.TypeDefinitionHandle(typeDefRow);
    }

    public void ReadFieldLayout(int rowId, out uint offset, out FieldDefinitionHandle field)
    {
        byte* ptr = GetRowPtr(TableIndex.FieldLayout, rowId);
        offset = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(ptr, 4));
        int fieldRow = ReadIndex(ptr + 4, _isField4Bytes);
        field = MetadataTokens.FieldDefinitionHandle(fieldRow);
    }

    public void ReadEventMap(int rowId, out TypeDefinitionHandle parent, out EventDefinitionHandle eventList)
    {
        byte* ptr = GetRowPtr(TableIndex.EventMap, rowId);
        int typeDefRow = ReadIndex(ptr, _isTypeDef4Bytes);
        int col = _isTypeDef4Bytes ? 4 : 2;
        int eventRow = ReadIndex(ptr + col, _isEvent4Bytes);
        parent = MetadataTokens.TypeDefinitionHandle(typeDefRow);
        eventList = MetadataTokens.EventDefinitionHandle(eventRow);
    }

    public void ReadPropertyMap(int rowId, out TypeDefinitionHandle parent, out PropertyDefinitionHandle propertyList)
    {
        byte* ptr = GetRowPtr(TableIndex.PropertyMap, rowId);
        int typeDefRow = ReadIndex(ptr, _isTypeDef4Bytes);
        int col = _isTypeDef4Bytes ? 4 : 2;
        int propRow = ReadIndex(ptr + col, _isProperty4Bytes);
        parent = MetadataTokens.TypeDefinitionHandle(typeDefRow);
        propertyList = MetadataTokens.PropertyDefinitionHandle(propRow);
    }

    public void ReadMethodSemantics(int rowId, out MethodSemanticsAttributes semantics, out MethodDefinitionHandle method, out EntityHandle association)
    {
        byte* ptr = GetRowPtr(TableIndex.MethodSemantics, rowId);
        semantics = (MethodSemanticsAttributes)BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(ptr, 2));
        int methodRow = ReadIndex(ptr + 2, _isMethodDef4Bytes);
        method = MetadataTokens.MethodDefinitionHandle(methodRow);

        int col = 2 + (_isMethodDef4Bytes ? 4 : 2);
        uint rawAssoc = ReadUInt(ptr + col, _isHasSemantics4Bytes);
        int tag = (int)(rawAssoc & 1);
        int assocRow = (int)(rawAssoc >> 1);
        association = (tag == 0) ? MetadataTokens.EventDefinitionHandle(assocRow) : MetadataTokens.PropertyDefinitionHandle(assocRow);
    }

    public void ReadMethodImpl(int rowId, out TypeDefinitionHandle type, out EntityHandle methodBody, out EntityHandle methodDeclaration)
    {
        byte* ptr = GetRowPtr(TableIndex.MethodImpl, rowId);
        int typeRow = ReadIndex(ptr, _isTypeDef4Bytes);
        type = MetadataTokens.TypeDefinitionHandle(typeRow);

        int col = _isTypeDef4Bytes ? 4 : 2;
        uint rawBody = ReadUInt(ptr + col, _isMethodDefOrRef4Bytes);
        int bodyTag = (int)(rawBody & 1);
        int bodyRow = (int)(rawBody >> 1);
        methodBody = (bodyTag == 0) ? MetadataTokens.MethodDefinitionHandle(bodyRow) : MetadataTokens.MemberReferenceHandle(bodyRow);

        col += _isMethodDefOrRef4Bytes ? 4 : 2;
        uint rawDecl = ReadUInt(ptr + col, _isMethodDefOrRef4Bytes);
        int declTag = (int)(rawDecl & 1);
        int declRow = (int)(rawDecl >> 1);
        methodDeclaration = (declTag == 0) ? MetadataTokens.MethodDefinitionHandle(declRow) : MetadataTokens.MemberReferenceHandle(declRow);
    }

    public void ReadImplMap(int rowId, out MethodImportAttributes attributes, out MethodDefinitionHandle method, out StringHandle importName, out ModuleReferenceHandle importScope)
    {
        byte* ptr = GetRowPtr(TableIndex.ImplMap, rowId);
        attributes = (MethodImportAttributes)BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(ptr, 2));

        uint rawMember = ReadUInt(ptr + 2, _isMemberForwarded4Bytes);
        int memberRow = (int)(rawMember >> 1);
        method = MetadataTokens.MethodDefinitionHandle(memberRow);

        int col = 2 + (_isMemberForwarded4Bytes ? 4 : 2);
        int stringOffset = ReadIndex(ptr + col, _isString4Bytes);
        importName = MetadataTokens.StringHandle(stringOffset);

        col += StringIndexSize;
        int modRefRow = ReadIndex(ptr + col, _isModuleRef4Bytes);
        importScope = MetadataTokens.ModuleReferenceHandle(modRefRow);
    }

    public void ReadFieldRva(int rowId, out uint rva, out FieldDefinitionHandle field)
    {
        byte* ptr = GetRowPtr(TableIndex.FieldRva, rowId);
        rva = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(ptr, 4));
        int fieldRow = ReadIndex(ptr + 4, _isField4Bytes);
        field = MetadataTokens.FieldDefinitionHandle(fieldRow);
    }

    public void ReadNestedClass(int rowId, out TypeDefinitionHandle nestedClass, out TypeDefinitionHandle enclosingClass)
    {
        byte* ptr = GetRowPtr(TableIndex.NestedClass, rowId);
        int nestedRow = ReadIndex(ptr, _isTypeDef4Bytes);
        int col = _isTypeDef4Bytes ? 4 : 2;
        int enclosingRow = ReadIndex(ptr + col, _isTypeDef4Bytes);
        nestedClass = MetadataTokens.TypeDefinitionHandle(nestedRow);
        enclosingClass = MetadataTokens.TypeDefinitionHandle(enclosingRow);
    }
}
