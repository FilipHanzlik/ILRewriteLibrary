using System;
using System.Collections.Generic;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata;
using System.Text;

namespace src
{
    public class IlWriter
    {
        PEReader _peReader;
        MetadataReader _reader; // Here the IlReader should be able to drop into place without a problem

       public void BuildOutput(string outputPath)
       {


       }
    }
}
