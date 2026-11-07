using System;
using System.Collections.Generic;
using System.Text;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace src
{
    public sealed class IlReader
    {
        private MetadataDeltaStore _delta;
        private MetadataReader _metadataReader;
        private PEReader _peReader;

        public MethodDefinitionHandle Entrypoint { get { throw new NotImplementedException(); } }

        internal IlReader(MetadataDeltaStore delta, PEReader peReader)
        {

            _delta = delta;
            _peReader = peReader;

            validatePEFormat(peReader);
            _metadataReader = peReader.GetMetadataReader();


        }

        private void validatePEFormat(PEReader peReader)
        {
            PEHeaders? headers = peReader.PEHeaders;
            if (headers is null) throw new InvalidOperationException($"PEHeaders were not found in the file");
            if (!headers.IsDll) throw new InvalidOperationException($"The provided file is not a .dll");
            if (!peReader.HasMetadata) throw new InvalidOperationException($"The provided .dll does not have CIL metadata");

            // CorHeader is present because HasMetadata = true
            if (!headers.CorHeader!.Flags.HasFlag(CorFlags.ILOnly)) throw new InvalidOperationException($"The .dll contains mixed code. IL Rewriter supports only pure CIL assemblies");

        }

    }
}
