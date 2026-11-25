using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;

namespace BatteryMonitor.UI.Views
{
    public sealed partial class SettingsPage : Page
    {
        private BatteryMonitor.Config _cfg;

        public SettingsPage()
        {
            InitializeComponent();
            NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
            Loaded += (s, e) => LoadValues();
        }

        private void LoadValues()
        {
            _cfg = BatteryMonitor.Config.Load();
            ThresholdBox.Value = _cfg.LowBatteryWarn;
            RecordMinutesBox.Value = _cfg.RecordMinutes;
            HistoryDaysBox.Value = _cfg.HistoryDays;
            EstimateToggle.IsOn = true;   // 新功能，暂不在旧 ini 里存
            ShowTempToggle.IsOn = _cfg.ShowTemp;
            ShowBtToggle.IsOn = _cfg.ShowBt;
        }

        private void Theme_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (ThemeCombo.SelectedItem is not ComboBoxItem item) return;
            var theme = (string)item.Tag;
            if (Application.Current is App app)
            {
                // 主题切换只影响本进程；RequestedTheme 在 App.xaml 已设 Dark
                SavedText.Text = theme == "Dark" ? "已固定深色主题" : "主题将在重启后生效";
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_cfg == null) return;
            _cfg.LowBatteryWarn = (int)ThresholdBox.Value;
            _cfg.RecordMinutes = (int)RecordMinutesBox.Value;
            _cfg.HistoryDays = (int)HistoryDaysBox.Value;
            _cfg.ShowTemp = ShowTempToggle.IsOn;
            _cfg.ShowBt = ShowBtToggle.IsOn;

            try
            {
                _cfg.Save(BatteryMonitor.AppPaths.Ini);
                SavedText.Text = "✓ 已保存";
            }
            catch (Exception ex) { SavedText.Text = "保存失败：" + ex.Message; }
        }
    }
}
