using LOGExtractor.Gba;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Reflection;

namespace MapViewer
{
    public partial class MainForm : Form
    {
        private record MapItem(string DisplayName, int Offset);
        private record MapConnector(int Left, int Top, int Right, int Bottom, int DestinationId);

        private ROM? rom;
        private MapItem? selectedMapItem;

        public MainForm()
        {
            InitializeComponent();
            AllowDrop = true;
            UxEventsToggle.Visible = false;
            UxMapList.Enabled = false;

            UxMapCanvas.Paint += UxMapCanvas_Paint;
            UxMapCanvas.ZoomChanged += UxMapCanvas_ZoomChanged;

            // Event handlers for the save buttons
            UxSaveButton.Click += UxSaveButton_Click;
            UxSaveAllButton.Click += UxSaveAllButton_Click;

            UxZoomOption.Items.Clear();
            foreach (var mode in Canvas.ZoomModes)
            {
                UxZoomOption.Items.Add($"{(mode * 100f)}%");
            }
            UxZoomOption.SelectedIndex = UxMapCanvas.ZoomMode;

            AutoLoadRomFile();
        }

        private void AutoLoadRomFile()
        {
            string executableFolder = Application.StartupPath;
            string[] romFiles = Directory.GetFiles(executableFolder, "*.gba");

            if (romFiles.Length == 1)
            {
                ReadMapsFromROM(romFiles[0]);
            }
            else if (romFiles.Length > 1)
            {
                using (OpenFileDialog ofd = new OpenFileDialog())
                {
                    ofd.Filter = "GBA ROM Files (*.gba)|*.gba";
                    ofd.Title = "Select a GBA ROM File";
                    ofd.InitialDirectory = executableFolder;

                    if (ofd.ShowDialog() == DialogResult.OK)
                    {
                        ReadMapsFromROM(ofd.FileName);
                    }
                }
            }
        }

        private void UxMapCanvas_ZoomChanged(int newZoom)
        {
            UxZoomOption.SelectedIndex = newZoom;
        }

        private void UxMapCanvas_Paint(object? sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;

            if (rom == null) return;
            if (selectedMapItem == null) return;

            try
            {
                rom.Seek(selectedMapItem.Offset + 0x2C);
                rom.Seek(rom.ReadPointer());

                int mapOffset = rom.ReadPointer();

                // map tileset
                var tilesets = MapRenderer.DrawTileset(rom, mapOffset);

                // layers
                rom.Seek(mapOffset + 0x14);
                var layer0 = MapRenderer.DrawLayer(rom, rom.ReadPointer(), tilesets);
                var layer1 = MapRenderer.DrawLayer(rom, rom.ReadPointer(), tilesets);
                var layer2 = MapRenderer.DrawLayer(rom, rom.ReadPointer(), tilesets);
                var layer3 = MapRenderer.DrawLayer(rom, rom.ReadPointer(), tilesets);

                // connectors
                rom.Seek(mapOffset + 0x40);
                var connectors = GetConnectors(rom);

                // map size
                rom.Seek(mapOffset + 0x8);
                int width = rom.ReadShort() + 240 - 1;  // gba viewport width
                int height = rom.ReadShort() + 160 - 1; // gba viewport height

                if (UxBG3Toggle.Checked)
                {
                    g.DrawImage(layer3, 0, 0);
                }
                if (UxBG2Toggle.Checked)
                {
                    g.DrawImage(layer2, 0, 0);
                }
                if (UxBG1Toggle.Checked)
                {
                    g.DrawImage(layer1, 0, 0);
                }
                if (UxBG0Toggle.Checked)
                {
                    g.DrawImage(layer0, 0, 0);
                }

                if (UxConnectorsToggle.Checked)
                {
                    foreach (var connector in connectors)
                    {
                        // +8 to rectangle X seems right
                        var rect = Rectangle.FromLTRB(connector.Left, connector.Top, connector.Right, connector.Bottom);
                        rect.X += 8;
                        g.FillRectangle(Brushes.Red, rect);
                        g.DrawString($"{connector.DestinationId:X4}", Font, Brushes.White, rect.X, rect.Y);
                    }
                }

                // Draw the green boundary rectangle
                g.DrawRectangle(new Pen(Color.Green, 2), 0, 0, width, height);
            }
            catch (Exception ex)
            {
                g.Clear(Color.Black);
                g.DrawString($"Could not render map:\r\n{selectedMapItem}\r\n{ex}", Font, Brushes.DarkRed, 5, 5);
                Debug.WriteLine(selectedMapItem);
            }
        }

        private static List<MapConnector> GetConnectors(ROM rom)
        {
            int count = rom.ReadInt();
            if (count == 0)
            {
                return new List<MapConnector>();
            }

            var list = new List<MapConnector>();

            rom.Seek(rom.ReadPointer());
            for (int i = 0; i < count; i++)
            {
                rom.PushPosition(rom.ReadPointer());
                rom.Skip(0x8);
                int left = rom.ReadShort();
                int top = rom.ReadShort();
                int right = rom.ReadShort();
                int bottom = rom.ReadShort();
                rom.Skip(0x1);
                int destId = rom.ReadShortBigEndian();
                rom.PopPosition();

                list.Add(new MapConnector(left, top, right, bottom, destId));
            }

            return list;
        }

        private void UxMapList_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (UxMapList.SelectedItem is MapItem map)
            {
                if (map == selectedMapItem)
                {
                    return;
                }
                UxMapOffsetLabel.Text = $"Selected Map Offset: 0x{(map.Offset | 0x08000000):X}";
                selectedMapItem = map;
            }
            else
            {
                UxMapOffsetLabel.Text = "Selected Map Offset: 0x0";
                selectedMapItem = null;
            }

            UxMapCanvas.Invalidate();
        }

        private void ReadMapsFromROM(string path)
        {
            rom = null;
            selectedMapItem = null;
            UxMapCanvas.Invalidate();

            MapRenderer.ResetCache();
            UxMapList.Items.Clear();
            UxMapList.Enabled = false;

            try
            {
                const int map_count = 0x1c4;

                rom = ROM.FromFile(path);
                int currentAddr = 0x08e2e0;

                var itemsList = new List<MapItem>();

                for (int i = 0; i < map_count; i++)
                {
                    rom.Seek(currentAddr);
                    int mapId = rom.ReadShortBigEndian();
                    rom.Skip(0xA);
                    int mapNameId = rom.ReadShort();
                    string mapName = string.Empty;
                    if (mapNameId != 0)
                    {
                        rom.PushPosition(0x06bce8 + (mapNameId * 4));
                        rom.Seek(rom.ReadPointer());
                        mapName = rom.ReadUnicodeString();
                        rom.PopPosition();
                    }
                    itemsList.Add(new MapItem($"{mapId:X4}: {mapName}", currentAddr));

                    currentAddr += 0x38;
                }

                UxMapList.Items.AddRange(itemsList.ToArray());
            }
            catch { }

            UxTotalMapsLabel.Text = $"Total Maps: {UxMapList.Items.Count}";
            UxMapList.DisplayMember = "DisplayName";
            UxMapList.Enabled = UxMapList.Items.Count > 0;
        }

        private void UxToggle_CheckChanged(object sender, EventArgs e)
        {
            UxMapCanvas.Invalidate();
        }

        private void UxZoomOption_SelectedIndexChanged(object sender, EventArgs e)
        {
            UxMapCanvas.ZoomMode = UxZoomOption.SelectedIndex;
        }

        private void UxResetView_Click(object sender, EventArgs e)
        {
            UxMapCanvas.ResetView();
        }

        protected override void OnDragEnter(DragEventArgs drgevent)
        {
            if (drgevent.Data != null && drgevent.Data.GetDataPresent(DataFormats.FileDrop))
            {
                drgevent.Effect = DragDropEffects.Copy;
            }
        }

        protected override void OnDragDrop(DragEventArgs drgevent)
        {
            if (drgevent.Data != null && drgevent.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                ReadMapsFromROM(files[0]);
            }
        }

        // Event handler for saving images of the currently selected map
        private void UxSaveButton_Click(object sender, EventArgs e)
        {
            if (rom == null || selectedMapItem == null) return;

            SaveMapImages(UxMapList.SelectedIndex);
        }

        private void UxSaveAllButton_Click(object sender, EventArgs e)
        {
            if (rom == null) return;

            for (int i = 0; i < UxMapList.Items.Count; i++)
            {
                UxMapList.SelectedIndex = i;
                SaveMapImages(i);
            }

            MessageBox.Show("Done", "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // Method to save images for a specific map based on its index
        private void SaveMapImages(int mapIndex)
        {
            if (rom == null) return;

            // Select the map item at the given index without affecting the UI selection
            if (UxMapList.Items[mapIndex] is not MapItem mapItem) return;

            string mapName = mapItem.DisplayName.Split(':')[1].Trim();

            if (string.IsNullOrEmpty(mapName))
            {
                mapName = mapIndex.ToString();
            }

            // Create a directory named after the index and map name
            string basePath = Path.Combine(Application.StartupPath, "maps", $"{mapIndex}_{mapName}");
            Directory.CreateDirectory(basePath);

            try
            {
                // Retrieve the image layers from the ROM
                rom.Seek(mapItem.Offset + 0x2C);
                rom.Seek(rom.ReadPointer());
                int mapOffset = rom.ReadPointer();

                // Retrieve and save the tileset
                var tilesets = MapRenderer.DrawTileset(rom, mapOffset);
                SaveTilesetImage(tilesets, $"{mapName}_tileset", basePath);

                // Draw the layers
                rom.Seek(mapOffset + 0x14);
                var layer0 = MapRenderer.DrawLayer(rom, rom.ReadPointer(), tilesets);
                var layer1 = MapRenderer.DrawLayer(rom, rom.ReadPointer(), tilesets);
                var layer2 = MapRenderer.DrawLayer(rom, rom.ReadPointer(), tilesets);
                var layer3 = MapRenderer.DrawLayer(rom, rom.ReadPointer(), tilesets);

                // Read the map size for dynamic cropping
                rom.Seek(mapOffset + 0x8);
                int width = rom.ReadShort() + 240 - 1;
                int height = rom.ReadShort() + 160 - 1;

                // Save the current toggle states
                bool bg0State = UxBG0Toggle.Checked;
                bool bg1State = UxBG1Toggle.Checked;
                bool bg2State = UxBG2Toggle.Checked;
                bool bg3State = UxBG3Toggle.Checked;

                // Check if cropping is enabled
                bool shouldCrop = UxCropCheckBox.Checked;
                Rectangle cropRect = new Rectangle(0, 0, width, height); // Use dynamic width and height

                // Crop and save each layer individually
                SetToggleState(false, false, false, true); // Only UxBG3Toggle enabled
                SaveLayerImage(shouldCrop ? CropImage(layer3, cropRect) : layer3, $"{mapName}_BG3", basePath);

                SetToggleState(false, false, true, false); // Only UxBG2Toggle enabled
                SaveLayerImage(shouldCrop ? CropImage(layer2, cropRect) : layer2, $"{mapName}_BG2", basePath);

                SetToggleState(false, true, false, false); // Only UxBG1Toggle enabled
                SaveLayerImage(shouldCrop ? CropImage(layer1, cropRect) : layer1, $"{mapName}_BG1", basePath);

                SetToggleState(true, false, false, false); // Only UxBG0Toggle enabled
                SaveLayerImage(shouldCrop ? CropImage(layer0, cropRect) : layer0, $"{mapName}_BG0", basePath);

                // Create and save the combined image
                SetToggleState(true, true, true, true); // All toggles enabled

                Bitmap combinedImage = new Bitmap(cropRect.Width, cropRect.Height);
                using (Graphics g = Graphics.FromImage(combinedImage))
                {
                    if (UxBG3Toggle.Checked) g.DrawImage(CropImage(layer3, cropRect), 0, 0);
                    if (UxBG2Toggle.Checked) g.DrawImage(CropImage(layer2, cropRect), 0, 0);
                    if (UxBG1Toggle.Checked) g.DrawImage(CropImage(layer1, cropRect), 0, 0);
                    if (UxBG0Toggle.Checked) g.DrawImage(CropImage(layer0, cropRect), 0, 0);
                }
                combinedImage.Save(Path.Combine(basePath, $"{mapName}_complete.png"), System.Drawing.Imaging.ImageFormat.Png);

                // Restore the original toggle states
                SetToggleState(bg0State, bg1State, bg2State, bg3State);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving images for map {mapName}: {ex.Message}");
            }
        }

        // Helper method to save the tileset image
        private void SaveTilesetImage(Dictionary<Range, Bitmap> tilesets, string name, string basePath)
        {
            foreach (var tileset in tilesets)
            {
                string filePath = Path.Combine(basePath, $"{name}_{tileset.Key.Start.Value}-{tileset.Key.End.Value}.png");
                tileset.Value.Save(filePath, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        // Helper method to save a single layer image
        private void SaveLayerImage(Bitmap layer, string name, string basePath)
        {
            if (layer == null) return;
            string filePath = Path.Combine(basePath, $"{name}.png");
            layer.Save(filePath, System.Drawing.Imaging.ImageFormat.Png);
        }

        // Helper method to crop an image
        private Bitmap CropImage(Bitmap source, Rectangle cropRect)
        {
            Bitmap croppedImage = new Bitmap(cropRect.Width, cropRect.Height);
            using (Graphics g = Graphics.FromImage(croppedImage))
            {
                g.DrawImage(source, new Rectangle(0, 0, cropRect.Width, cropRect.Height), cropRect, GraphicsUnit.Pixel);
            }
            return croppedImage;
        }

        // Helper method to set the toggle states
        private void SetToggleState(bool bg0, bool bg1, bool bg2, bool bg3)
        {
            UxBG0Toggle.Checked = bg0;
            UxBG1Toggle.Checked = bg1;
            UxBG2Toggle.Checked = bg2;
            UxBG3Toggle.Checked = bg3;
        }
    }
}
