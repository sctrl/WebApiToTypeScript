﻿using System.Collections.Generic;

namespace WebApiToTypeScript
{
    public class Config
    {
        public string WebApiModuleFileName { get; set; }

        public string EndpointsOutputDirectory { get; set; }

        public string EndpointsFileName { get; set; }
            = "Endpoints.ts";

        public string EndpointsNamespace { get; set; }
            = "Endpoints";

        public bool GenerateInterfaces { get; set; }
            = false;

        public bool GenerateEnums { get; set; }
            = true;

        public string EnumsOutputDirectory { get; set; }

        public string EnumsFileName { get; set; }
            = "Enums.ts";

        public string EnumsNamespace { get; set; }
            = "Enums";

        public bool ScanOtherModules { get; set; }
            = true;

        public bool WriteNamespaceAsModule { get; set; }
            = false;

        public List<TypeMapping> TypeMappings { get; set; }
            = new List<TypeMapping>();
    }
}