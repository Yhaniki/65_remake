using System;
using TMPro;
using UnityEngine;

namespace Sdo.UI.Util
{
    /// <summary>
    /// A simple [◀ value ▶] option cycler — used instead of TMP_Dropdown (which is fiddly to build
    /// procedurally) for stage / resolution / language / display-mode / mode selectors.
    /// </summary>
    public sealed class Cycler : MonoBehaviour
    {
        private TMP_Text _label;
        private string[] _options;
        public int Index { get; private set; }
        public event Action<int> Changed;

        public string Current => (_options != null && Index >= 0 && Index < _options.Length) ? _options[Index] : "";

        public void Init(TMP_Text label, string[] options, int start)
        {
            _label = label;
            SetOptions(options, start);
        }

        public void SetOptions(string[] options, int start)
        {
            _options = options ?? new string[0];
            Index = Mathf.Clamp(start, 0, Mathf.Max(0, _options.Length - 1));
            Refresh();
        }

        public void Set(int i, bool notify = true)
        {
            if (_options == null || _options.Length == 0) return;
            Index = (i % _options.Length + _options.Length) % _options.Length;
            Refresh();
            if (notify) Changed?.Invoke(Index);
        }

        public void Next() => Set(Index + 1);
        public void Prev() => Set(Index - 1);

        private void Refresh()
        {
            if (_label != null) _label.text = Current;
        }
    }
}
