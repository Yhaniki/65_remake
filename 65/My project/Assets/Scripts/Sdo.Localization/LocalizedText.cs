using TMPro;
using UnityEngine;

namespace Sdo.Localization
{
    /// <summary>
    /// Binds a TMP_Text to a localization key and re-resolves whenever the language changes.
    /// For dynamic values (score, BPM, counts) call <see cref="SetArgs"/> with the format args.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LocalizedText : MonoBehaviour
    {
        public string key;
        private object[] _args;
        private TMP_Text _tmp;

        private void Awake() => _tmp = GetComponent<TMP_Text>();

        private void OnEnable()
        {
            LocalizationManager.LanguageChanged += Resolve;
            Resolve();
        }

        private void OnDisable() => LocalizationManager.LanguageChanged -= Resolve;

        public void SetKey(string k) { key = k; Resolve(); }

        public void SetArgs(params object[] args) { _args = args; Resolve(); }

        private void Resolve()
        {
            if (_tmp == null) _tmp = GetComponent<TMP_Text>();
            if (_tmp == null || string.IsNullOrEmpty(key)) return;
            _tmp.text = _args == null
                ? LocalizationManager.Get(key)
                : LocalizationManager.Get(key, _args);
        }
    }
}
