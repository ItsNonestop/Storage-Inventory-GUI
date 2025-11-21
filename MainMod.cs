using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(StorageInventoryGUI.MainMod), "Storage Inventory GUI", "1.0.0", "ItsNonestop")]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace StorageInventoryGUI
{
    public sealed class MainMod : MelonMod
    {
        private InventoryGui _inventoryGui = null!;
        private bool _showGui;
        private bool _lastShowGui;

        public override void OnInitializeMelon()
        {

            _inventoryGui = new InventoryGui();
        }

        public override void OnUpdate()
        {

            if (Input.GetKeyDown(KeyCode.F6))
            {
                _showGui = !_showGui;
            }

            if (_showGui != _lastShowGui)
            {
                _inventoryGui.SetVisible(_showGui);
                _lastShowGui = _showGui;
            }

            _inventoryGui.OnUpdate(_showGui);
        }

        public override void OnGUI()
        {
            if (!_showGui || _inventoryGui == null)
                return;

            _inventoryGui.Draw();
        }
    }
}


