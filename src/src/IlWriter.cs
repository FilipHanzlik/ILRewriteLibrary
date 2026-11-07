using System;
using System.Collections.Generic;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata;
using System.Text;

namespace src
{
    public class IlWriter
    {
        /*
         Current limitations:
        - No support for strong name signature (strongNameSignatureSize=0 in ManagedPeBuilder. Assembly flag for public key is reset. The public key is removed from the blob.)
        - Generalizing the target machine when stripping R2R (to decide if its wanted and feasable) 
        - No rewriting for the PEHeaders (Subsystem, DllCharacteristics, ImageCharacteristics): Shouldn't be difficult to implmement if needed though
        - Only .dll files are supported (not .exe): Accepting .exe files should hopefully just be a matter of some work with headers  
        - Trusts IO input output without error handling (to some extent)
         */

        MetadataDeltaStore _deltaStore;
        PEReader _peReader;
        IlReader _reader;

       public void BuildOutput(string outputPath)
       {
            ManagedPEBuilder peBuilder = new ManagedPEBuilder(
                    header: ConstructPeHeaderBuilder(),
                    strongNameSignatureSize: _deltaStore.signAssemby ? throw new NotImplementedException() : 0,
                    entryPoint: _reader.Entrypoint,
                    flags: ConstructCorFlags()
            );

       }

        private PEHeaderBuilder ConstructPeHeaderBuilder()
        {
            CoffHeader coffHeader = _peReader.PEHeaders.CoffHeader;
            
            // This is the part of the peHeader that the specification describes as "Optional Header"
            // When loading the file the context checks that the file is a .dll image, which guarantees the presence of this part of the header
            PEHeader peHeader = _peReader.PEHeaders.PEHeader!;

            // ECMA Partition II. Section 25.2.
            // The relocStripped shall be 0
            // The LineNumsStripped shall be 0
            // 
            // Machine and Bit32Machine flags are not compliant with the ecma standard -> they should be just ported from the original
            //
            // Further the standard mandates: For the flags not mentioned above, flags 0x0010, 0x0020, 0x0400 and 0x0800 are implementationspecific, and all others should be zero (§II.24.1).
            //
            // PE Format referece:
            // AggressiveWSTrim: This flag is deprecated and should be zero
            // BytesReversedLo: This flag is deprecated and should be zero
            // BytesReversedHi: This flag is deprecated and should be zero
            // This aims to produce a ecma335 complient assembly in case of a false flag in the input
            const Characteristics characteristicsKillMask =
           ~(
                Characteristics.RelocsStripped |
                Characteristics.LineNumsStripped |
                Characteristics.LocalSymsStripped |
                Characteristics.AggressiveWSTrim |
                Characteristics.BytesReversedLo |
                Characteristics.DebugStripped |
                Characteristics.BytesReversedHi
            );

            const Characteristics EnforceMask =
                Characteristics.ExecutableImage |
                Characteristics.Dll;

  

            return new PEHeaderBuilder(
                machine: coffHeader.Machine,
                sectionAlignment: peHeader.SectionAlignment,
                fileAlignment: peHeader.FileAlignment,
                imageBase: peHeader.ImageBase,
                majorLinkerVersion: peHeader.MajorLinkerVersion,
                minorLinkerVersion: peHeader.MinorLinkerVersion,
                majorOperatingSystemVersion: peHeader.MajorOperatingSystemVersion,
                minorOperatingSystemVersion: peHeader.MinorOperatingSystemVersion,
                majorImageVersion: peHeader.MajorImageVersion,
                minorImageVersion: peHeader.MinorImageVersion,
                majorSubsystemVersion: peHeader.MajorSubsystemVersion,
                minorSubsystemVersion: peHeader.MinorSubsystemVersion,
                subsystem: peHeader.Subsystem,
                dllCharacteristics: peHeader.DllCharacteristics,
                imageCharacteristics: (coffHeader.Characteristics & characteristicsKillMask) | EnforceMask,
                sizeOfStackReserve: peHeader.SizeOfStackReserve,
                sizeOfStackCommit: peHeader.SizeOfStackCommit,
                sizeOfHeapReserve: peHeader.SizeOfHeapReserve,
                sizeOfHeapCommit: peHeader.SizeOfHeapCommit

            );
        }

        private CorFlags ConstructCorFlags(){

            // The existence of the CorHeader is guaranteed by the input criteria of the IlEditContext
            CorHeader corHeader = _peReader.PEHeaders.CorHeader!;

            /*
             * https://learn.microsoft.com/en-us/dotnet/api/system.reflection.portableexecutable.corflags?view=net-11.0-pp
             * Specifies that NativeEntryPoint shall be 0
             * TrackDebugData shall be 0
             * 
             * This Il editing prodecure does not support native code anyways
             */
            CorFlags corFlagsKillMask = ~(
                CorFlags.NativeEntryPoint | 
                CorFlags.TrackDebugData |
                (_deltaStore.signAssemby ? (CorFlags)0x0 : CorFlags.StrongNameSigned)
            );

            CorFlags corFlagsEnforceMask =
                CorFlags.ILOnly | 
                (_deltaStore.signAssemby ? CorFlags.StrongNameSigned : (CorFlags)0x0);


            return (corHeader.Flags & corFlagsKillMask) | corFlagsEnforceMask;
        }
    }
}
