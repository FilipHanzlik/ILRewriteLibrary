using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace src
{
    public class IlEditContext
    {
        /* 
         Misses:
        - Implement IDisposable: IlEditContext as the master owner of the ressources
         */

        private IlReader _reader;
        private IlWriter _writer;
        private IlEditor _editor;

        public IlReader Reader { get { return _reader; } }
        public IlWriter Writer { get { return _writer; } }
        public IlEditor Editor { get { return _editor; } }

        public IlEditContext(string path)
        {
        }

        private static void  LoadFile(string path)
        {
            var stream = new FileStream(
               path,
               FileMode.Open,
               FileAccess.Read,
               FileShare.Read);

            var peReader = new PEReader(stream, PEStreamOptions.PrefetchEntireImage);

        }
    }
}
