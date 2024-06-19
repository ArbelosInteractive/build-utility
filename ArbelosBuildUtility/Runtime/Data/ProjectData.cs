using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Arbelos.BuildUtility.Runtime
{
    [CreateAssetMenu(fileName = "BuildUtilityProjectData", menuName ="Arbelos Build Utility/Create Project Data")]
    public class ProjectData : ScriptableObject
    {
        public string azureStorageAccountName;
        public string azureStorageAccountSharedKey;
        public string azureStorageAccountURL;
        public string androidKeyStoreFilePath;
        public string androidKeyStorePassword;
        public string androidKeyStoreAliasName;
        public string androidKeyStoreAliasPassword;
    }
}
