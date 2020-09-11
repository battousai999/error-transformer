using System;
using System.Collections.Generic;
using System.Text;

namespace error_transformer
{
    public class Args
    {
        public string InputFolder { get; set; }
        public string OutputFolder { get; set; }
        public bool OutputUnzipped { get; set; }
        public bool OnlyIncludeMainLogFile { get; set; }
    }
}
