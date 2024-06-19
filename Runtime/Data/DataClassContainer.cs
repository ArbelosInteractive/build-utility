using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Arbelos.BuildUtility.Runtime
{
    [CreateAssetMenu(fileName = "GameAddressableData", menuName = "Arbelos Build Utility/Create Addressable Data Asset")]
    public class GameAddressableData:ScriptableObject
    {
        public string profileName;
        public string profileId;
        public List<AddressableCRCEntry> AddressableCRCList = new List<AddressableCRCEntry>();
    }

    [System.Serializable]
    public class AddressableCRCEntry
    {
        public string key;
        public uint value;

        public AddressableCRCEntry(string key, uint value)
        {
            this.key = key;
            this.value = value;
        }
    }
}
