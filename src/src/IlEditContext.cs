using System.Reflection.PortableExecutable;
using Sytem.Reflection.Metadata;

namespace src
{
    public class IlEditContext
    {
        private IlReader _reader;
        private IlWriter _writer;
        private IlEditor _editor;

        public IlEditContext(string path)
        {
            var handle = File.OpenHandle(path);
        }
    }
}
