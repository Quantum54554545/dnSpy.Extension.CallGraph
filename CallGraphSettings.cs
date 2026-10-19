using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using dnSpy.CallGraph.Core;
using dnSpy.Contracts.Settings;
using dnSpy.Contracts.Settings.Dialog;

namespace dnSpy.CallGraph {
	[Export, PartCreationPolicy(CreationPolicy.Shared)]
	sealed class CallGraphSettings {
		static readonly Guid SettingsGuid = new Guid("D4DB30D1-A37A-4EB6-9D3D-219AC62EA8C4");
		readonly ISettingsService settings_service;

		public event EventHandler? Changed;

		public int MaximumLines { get; private set; } = 0;
		public int EffectiveMaximumLines => MaximumLines <= 0 ? 80 : MaximumLines;
		public double BlockWidth { get; private set; } = 430;
		public bool WrapLongLines { get; private set; } = true;
		public bool SyntaxHighlighting { get; private set; } = true;
		public bool HideSystemMethods { get; private set; }
		public bool HideAccessors { get; private set; }
		public bool HideConstructors { get; private set; }
		public bool OnlyCurrentNamespace { get; private set; }
		public bool OnlyCurrentType { get; private set; }
		public bool RequireMethodBody { get; private set; }
		public int MinimumCallSiteCount { get; private set; } = 1;
		public bool CleanAssemblyExplorerOnLaunch { get; private set; }
		public bool ShortcutEnabled { get; private set; } = true;
		public ModifierKeys ShortcutModifiers { get; private set; } = ModifierKeys.Control | ModifierKeys.Alt;
		public Key ShortcutKey { get; private set; } = Key.G;
		public string ShortcutDisplayText => ShortcutEnabled ? FormatShortcut(ShortcutModifiers, ShortcutKey) : string.Empty;

		[ImportingConstructor]
		public CallGraphSettings(ISettingsService settings_service) {
			this.settings_service = settings_service;
			var section = settings_service.GetOrCreateSection(SettingsGuid);
			MaximumLines = NormalizeLines(section.Attribute<int?>(nameof(MaximumLines)) ?? MaximumLines);
			BlockWidth = Clamp(section.Attribute<double?>(nameof(BlockWidth)) ?? BlockWidth, 320, 900);
			WrapLongLines = section.Attribute<bool?>(nameof(WrapLongLines)) ?? WrapLongLines;
			SyntaxHighlighting = section.Attribute<bool?>(nameof(SyntaxHighlighting)) ?? SyntaxHighlighting;
			HideSystemMethods = section.Attribute<bool?>(nameof(HideSystemMethods)) ?? HideSystemMethods;
			HideAccessors = section.Attribute<bool?>(nameof(HideAccessors)) ?? HideAccessors;
			HideConstructors = section.Attribute<bool?>(nameof(HideConstructors)) ?? HideConstructors;
			OnlyCurrentNamespace = section.Attribute<bool?>(nameof(OnlyCurrentNamespace)) ?? OnlyCurrentNamespace;
			OnlyCurrentType = section.Attribute<bool?>(nameof(OnlyCurrentType)) ?? OnlyCurrentType;
			RequireMethodBody = section.Attribute<bool?>(nameof(RequireMethodBody)) ?? RequireMethodBody;
			MinimumCallSiteCount = NormalizeMinimumCallSites(section.Attribute<int?>(nameof(MinimumCallSiteCount)) ?? MinimumCallSiteCount);
			CleanAssemblyExplorerOnLaunch = section.Attribute<bool?>(nameof(CleanAssemblyExplorerOnLaunch)) ?? CleanAssemblyExplorerOnLaunch;
			ShortcutEnabled = section.Attribute<bool?>(nameof(ShortcutEnabled)) ?? ShortcutEnabled;
			ShortcutModifiers = NormalizeModifiers((ModifierKeys)(section.Attribute<int?>(nameof(ShortcutModifiers)) ?? (int)ShortcutModifiers));
			ShortcutKey = NormalizeKey((Key)(section.Attribute<int?>(nameof(ShortcutKey)) ?? (int)ShortcutKey));
		}

		public CallGraphFilterOptions CreateFilterOptions() => new CallGraphFilterOptions(HideSystemMethods,
			HideAccessors, HideConstructors, OnlyCurrentNamespace, OnlyCurrentType, RequireMethodBody, MinimumCallSiteCount);

		public void Apply(int maximum_lines, double block_width, bool wrap_long_lines, bool syntax_highlighting,
			bool hide_system_methods, bool hide_accessors, bool hide_constructors, bool only_current_namespace,
			bool only_current_type, bool require_method_body, int minimum_call_site_count,
			bool clean_assembly_explorer_on_launch, bool shortcut_enabled, ModifierKeys shortcut_modifiers, Key shortcut_key) {
			MaximumLines = NormalizeLines(maximum_lines);
			BlockWidth = Clamp(block_width, 320, 900);
			WrapLongLines = wrap_long_lines;
			SyntaxHighlighting = syntax_highlighting;
			HideSystemMethods = hide_system_methods;
			HideAccessors = hide_accessors;
			HideConstructors = hide_constructors;
			OnlyCurrentNamespace = only_current_namespace;
			OnlyCurrentType = only_current_type;
			RequireMethodBody = require_method_body;
			MinimumCallSiteCount = NormalizeMinimumCallSites(minimum_call_site_count);
			CleanAssemblyExplorerOnLaunch = clean_assembly_explorer_on_launch;
			ShortcutEnabled = shortcut_enabled;
			ShortcutModifiers = NormalizeModifiers(shortcut_modifiers);
			ShortcutKey = NormalizeKey(shortcut_key);
			if (ShortcutModifiers == ModifierKeys.None)
				ShortcutModifiers = ModifierKeys.Control;

			var section = settings_service.RecreateSection(SettingsGuid);
			section.Attribute(nameof(MaximumLines), MaximumLines);
			section.Attribute(nameof(BlockWidth), BlockWidth);
			section.Attribute(nameof(WrapLongLines), WrapLongLines);
			section.Attribute(nameof(SyntaxHighlighting), SyntaxHighlighting);
			section.Attribute(nameof(HideSystemMethods), HideSystemMethods);
			section.Attribute(nameof(HideAccessors), HideAccessors);
			section.Attribute(nameof(HideConstructors), HideConstructors);
			section.Attribute(nameof(OnlyCurrentNamespace), OnlyCurrentNamespace);
			section.Attribute(nameof(OnlyCurrentType), OnlyCurrentType);
			section.Attribute(nameof(RequireMethodBody), RequireMethodBody);
			section.Attribute(nameof(MinimumCallSiteCount), MinimumCallSiteCount);
			section.Attribute(nameof(CleanAssemblyExplorerOnLaunch), CleanAssemblyExplorerOnLaunch);
			section.Attribute(nameof(ShortcutEnabled), ShortcutEnabled);
			section.Attribute(nameof(ShortcutModifiers), (int)ShortcutModifiers);
			section.Attribute(nameof(ShortcutKey), (int)ShortcutKey);
			Changed?.Invoke(this, EventArgs.Empty);
		}

		static int NormalizeLines(int value) => value <= 0 ? 0 : (int)Clamp(value, 1, 500);
		static int NormalizeMinimumCallSites(int value) => Math.Max(1, Math.Min(1000, value));
		static double Clamp(double value, double minimum, double maximum) => Math.Max(minimum, Math.Min(maximum, value));
		static ModifierKeys NormalizeModifiers(ModifierKeys value) => value & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift);
		static Key NormalizeKey(Key value) => value > Key.None && value <= Key.OemClear ? value : Key.G;

		static string FormatShortcut(ModifierKeys modifiers, Key key) {
			var parts = new List<string>(4);
			if ((modifiers & ModifierKeys.Control) != 0)
				parts.Add("Ctrl");
			if ((modifiers & ModifierKeys.Alt) != 0)
				parts.Add("Alt");
			if ((modifiers & ModifierKeys.Shift) != 0)
				parts.Add("Shift");
			parts.Add(DisplayKeyName(key));
			return string.Join("+", parts);
		}

		internal static string DisplayKeyName(Key key) {
			if (key >= Key.D0 && key <= Key.D9)
				return ((int)key - (int)Key.D0).ToString(CultureInfo.InvariantCulture);
			return key.ToString();
		}
	}

	[Export(typeof(IAppSettingsPageProvider))]
	sealed class CallGraphSettingsPageProvider : IAppSettingsPageProvider {
		readonly CallGraphSettings settings;

		[ImportingConstructor]
		public CallGraphSettingsPageProvider(CallGraphSettings settings) => this.settings = settings;

		public IEnumerable<AppSettingsPage> Create() {
			yield return new CallGraphSettingsPage(settings);
		}
	}

	sealed class CallGraphSettingsPage : AppSettingsPage {
		static readonly Guid PageGuid = new Guid("1CDBA569-E3BD-43C9-87A7-9FD3AB93D370");
		static ControlTemplate? slider_template;
		readonly CallGraphSettings settings;
		readonly ComboBox lines_combo_box;
		readonly Slider width_slider;
		readonly TextBlock width_value;
		readonly CheckBox wrap_check_box;
		readonly CheckBox highlight_check_box;
		readonly CheckBox hide_system_methods_check_box;
		readonly CheckBox hide_accessors_check_box;
		readonly CheckBox hide_constructors_check_box;
		readonly CheckBox only_current_namespace_check_box;
		readonly CheckBox only_current_type_check_box;
		readonly CheckBox require_method_body_check_box;
		readonly ComboBox minimum_call_sites_combo_box;
		readonly CheckBox clean_explorer_check_box;
		readonly CheckBox shortcut_enabled_check_box;
		readonly CheckBox shortcut_ctrl_check_box;
		readonly CheckBox shortcut_alt_check_box;
		readonly CheckBox shortcut_shift_check_box;
		readonly ComboBox shortcut_key_combo_box;

		public override Guid Guid => PageGuid;
		public override double Order => 1800;
		public override string Title => UiText.CallGraph;
		public override object UIObject { get; }

		public CallGraphSettingsPage(CallGraphSettings settings) {
			this.settings = settings;
			var panel = new StackPanel { Margin = new Thickness(16, 12, 20, 16), MaxWidth = 650 };
			panel.Children.Add(new TextBlock {
				Text = UiText.SettingsHeading,
				FontSize = 16,
				FontWeight = FontWeights.SemiBold,
				Margin = new Thickness(0, 0, 0, 12),
			});

			panel.Children.Add(Label(UiText.LinesTitle, UiText.LinesDescription));
			lines_combo_box = new ComboBox { Width = 150, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 3, 0, 12) };
			lines_combo_box.Items.Add(new LineChoice(0, UiText.AutoLines));
			foreach (int value in new[] { 5, 10, 20, 40, 80, 160 })
				lines_combo_box.Items.Add(new LineChoice(value, value.ToString(CultureInfo.InvariantCulture)));
			SelectLines(settings.MaximumLines);
			panel.Children.Add(lines_combo_box);

			panel.Children.Add(Label(UiText.BlockWidth, UiText.BlockWidthDescription));
			var width_row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 8) };
			width_slider = CreateSlider(320, 900, 20, settings.BlockWidth, 360);
			width_value = ValueText();
			width_slider.ValueChanged += (_, __) => width_value.Text = Math.Round(width_slider.Value).ToString(CultureInfo.InvariantCulture) + " px";
			width_row.Children.Add(width_slider);
			width_row.Children.Add(width_value);
			panel.Children.Add(width_row);
			width_value.Text = Math.Round(width_slider.Value).ToString(CultureInfo.InvariantCulture) + " px";

			wrap_check_box = new CheckBox { Content = UiText.WrapLines, IsChecked = settings.WrapLongLines, Margin = new Thickness(0, 3, 0, 7) };
			highlight_check_box = new CheckBox { Content = UiText.SyntaxHighlighting, IsChecked = settings.SyntaxHighlighting, Margin = new Thickness(0, 0, 0, 16) };
			panel.Children.Add(wrap_check_box);
			panel.Children.Add(highlight_check_box);

			panel.Children.Add(SectionTitle(UiText.GraphFilters));
			panel.Children.Add(new TextBlock {
				Text = UiText.GraphFiltersDescription,
				Opacity = 0.72,
				TextWrapping = TextWrapping.Wrap,
				Margin = new Thickness(0, 0, 0, 8),
			});
			hide_system_methods_check_box = FilterCheckBox(UiText.HideSystemMethods, settings.HideSystemMethods);
			hide_accessors_check_box = FilterCheckBox(UiText.HideAccessors, settings.HideAccessors);
			hide_constructors_check_box = FilterCheckBox(UiText.HideConstructors, settings.HideConstructors);
			only_current_namespace_check_box = FilterCheckBox(UiText.OnlyCurrentNamespace, settings.OnlyCurrentNamespace);
			only_current_type_check_box = FilterCheckBox(UiText.OnlyCurrentType, settings.OnlyCurrentType);
			require_method_body_check_box = FilterCheckBox(UiText.RequireMethodBody, settings.RequireMethodBody);
			panel.Children.Add(hide_system_methods_check_box);
			panel.Children.Add(hide_accessors_check_box);
			panel.Children.Add(hide_constructors_check_box);
			panel.Children.Add(only_current_namespace_check_box);
			panel.Children.Add(only_current_type_check_box);
			panel.Children.Add(require_method_body_check_box);
			var call_site_row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 16) };
			call_site_row.Children.Add(new TextBlock { Text = UiText.MinimumCallSites, VerticalAlignment = VerticalAlignment.Center });
			minimum_call_sites_combo_box = new ComboBox { Width = 72, Margin = new Thickness(10, 0, 0, 0) };
			foreach (int value in new[] { 1, 2, 3, 5, 10, 20 })
				minimum_call_sites_combo_box.Items.Add(value);
			minimum_call_sites_combo_box.SelectedItem = settings.MinimumCallSiteCount;
			if (minimum_call_sites_combo_box.SelectedItem is null)
				minimum_call_sites_combo_box.SelectedIndex = 0;
			call_site_row.Children.Add(minimum_call_sites_combo_box);
			panel.Children.Add(call_site_row);

			panel.Children.Add(SectionTitle(UiText.KeyboardShortcut));
			shortcut_enabled_check_box = new CheckBox {
				Content = UiText.EnableShortcut,
				IsChecked = settings.ShortcutEnabled,
				Margin = new Thickness(0, 0, 0, 8),
			};
			panel.Children.Add(shortcut_enabled_check_box);
			var shortcut_row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
			shortcut_ctrl_check_box = ModifierCheckBox("Ctrl", (settings.ShortcutModifiers & ModifierKeys.Control) != 0);
			shortcut_alt_check_box = ModifierCheckBox("Alt", (settings.ShortcutModifiers & ModifierKeys.Alt) != 0);
			shortcut_shift_check_box = ModifierCheckBox("Shift", (settings.ShortcutModifiers & ModifierKeys.Shift) != 0);
			shortcut_key_combo_box = new ComboBox { Width = 115, HorizontalAlignment = HorizontalAlignment.Left };
			foreach (var choice in KeyChoice.CreateChoices(settings.ShortcutKey))
				shortcut_key_combo_box.Items.Add(choice);
			SelectShortcutKey(settings.ShortcutKey);
			shortcut_row.Children.Add(shortcut_ctrl_check_box);
			shortcut_row.Children.Add(shortcut_alt_check_box);
			shortcut_row.Children.Add(shortcut_shift_check_box);
			shortcut_row.Children.Add(shortcut_key_combo_box);
			panel.Children.Add(shortcut_row);
			panel.Children.Add(new TextBlock {
				Text = UiText.ShortcutDescription,
				Opacity = 0.72,
				TextWrapping = TextWrapping.Wrap,
				Margin = new Thickness(0, 0, 0, 16),
			});

			panel.Children.Add(SectionTitle(UiText.AssemblyExplorer));
			clean_explorer_check_box = new CheckBox {
				Content = UiText.CleanExplorer,
				IsChecked = settings.CleanAssemblyExplorerOnLaunch,
				Margin = new Thickness(0, 0, 0, 5),
			};
			panel.Children.Add(clean_explorer_check_box);
			panel.Children.Add(new TextBlock {
				Text = UiText.CleanExplorerDescription,
				Opacity = 0.72,
				TextWrapping = TextWrapping.Wrap,
			});

			UIObject = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
		}

		public override void OnApply() {
			var line_choice = lines_combo_box.SelectedItem as LineChoice;
			var modifiers = ModifierKeys.None;
			if (shortcut_ctrl_check_box.IsChecked == true)
				modifiers |= ModifierKeys.Control;
			if (shortcut_alt_check_box.IsChecked == true)
				modifiers |= ModifierKeys.Alt;
			if (shortcut_shift_check_box.IsChecked == true)
				modifiers |= ModifierKeys.Shift;
			if (modifiers == ModifierKeys.None) {
				modifiers = ModifierKeys.Control;
				shortcut_ctrl_check_box.IsChecked = true;
			}
			var key_choice = shortcut_key_combo_box.SelectedItem as KeyChoice;
			settings.Apply(line_choice?.Value ?? 0, Math.Round(width_slider.Value),
				wrap_check_box.IsChecked == true, highlight_check_box.IsChecked == true,
				hide_system_methods_check_box.IsChecked == true, hide_accessors_check_box.IsChecked == true,
				hide_constructors_check_box.IsChecked == true, only_current_namespace_check_box.IsChecked == true,
				only_current_type_check_box.IsChecked == true, require_method_body_check_box.IsChecked == true,
				minimum_call_sites_combo_box.SelectedItem is int minimum_call_sites ? minimum_call_sites : 1,
				clean_explorer_check_box.IsChecked == true, shortcut_enabled_check_box.IsChecked == true,
				modifiers, key_choice?.Value ?? Key.G);
		}

		void SelectLines(int value) {
			foreach (LineChoice choice in lines_combo_box.Items) {
				if (choice.Value == value) {
					lines_combo_box.SelectedItem = choice;
					return;
				}
			}
			lines_combo_box.SelectedIndex = 0;
		}

		void SelectShortcutKey(Key value) {
			foreach (KeyChoice choice in shortcut_key_combo_box.Items) {
				if (choice.Value == value) {
					shortcut_key_combo_box.SelectedItem = choice;
					return;
				}
			}
			shortcut_key_combo_box.SelectedIndex = 0;
		}

		static FrameworkElement Label(string title, string description) {
			var panel = new StackPanel();
			panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
			panel.Children.Add(new TextBlock { Text = description, Opacity = 0.72, TextWrapping = TextWrapping.Wrap });
			return panel;
		}

		static TextBlock SectionTitle(string text) => new TextBlock {
			Text = text,
			FontSize = 16,
			FontWeight = FontWeights.SemiBold,
			Margin = new Thickness(0, 4, 0, 10),
		};

		static CheckBox ModifierCheckBox(string text, bool is_checked) => new CheckBox {
			Content = text,
			IsChecked = is_checked,
			Margin = new Thickness(0, 2, 14, 0),
			VerticalAlignment = VerticalAlignment.Center,
		};

		static CheckBox FilterCheckBox(string text, bool is_checked) => new CheckBox {
			Content = text,
			IsChecked = is_checked,
			Margin = new Thickness(0, 0, 0, 6),
		};

		internal static Slider CreateSlider(double minimum, double maximum, double tick, double value, double width) {
			var slider = new Slider {
				Minimum = minimum,
				Maximum = maximum,
				TickFrequency = tick,
				IsSnapToTickEnabled = true,
				IsMoveToPointEnabled = true,
				Value = value,
				Width = width,
				Height = 24,
				Template = slider_template ??= CreateSliderTemplate(),
			};
			slider.SetResourceReference(Control.ForegroundProperty, "HyperlinkNormal");
			return slider;
		}

		static ControlTemplate CreateSliderTemplate() => (ControlTemplate)XamlReader.Parse(@"
<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                 xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
                 TargetType='{x:Type Slider}'>
  <Grid Height='24' Background='Transparent' SnapsToDevicePixels='True'>
    <Border Height='4' VerticalAlignment='Center' CornerRadius='2'
            Background='{DynamicResource CommonControlsTextBoxBorder}' />
    <Track x:Name='PART_Track' Margin='0,0' VerticalAlignment='Center'
           Minimum='{TemplateBinding Minimum}' Maximum='{TemplateBinding Maximum}'
           Value='{TemplateBinding Value}' Orientation='{TemplateBinding Orientation}'
           IsDirectionReversed='{TemplateBinding IsDirectionReversed}'>
      <Track.DecreaseRepeatButton>
        <RepeatButton Command='{x:Static Slider.DecreaseLarge}' Focusable='False'>
          <RepeatButton.Template>
            <ControlTemplate TargetType='{x:Type RepeatButton}'>
              <Border Height='4' CornerRadius='2' VerticalAlignment='Center'
                      Background='{DynamicResource HyperlinkNormal}' />
            </ControlTemplate>
          </RepeatButton.Template>
        </RepeatButton>
      </Track.DecreaseRepeatButton>
      <Track.IncreaseRepeatButton>
        <RepeatButton Command='{x:Static Slider.IncreaseLarge}' Focusable='False'>
          <RepeatButton.Template>
            <ControlTemplate TargetType='{x:Type RepeatButton}'>
              <Border Background='Transparent' />
            </ControlTemplate>
          </RepeatButton.Template>
        </RepeatButton>
      </Track.IncreaseRepeatButton>
      <Track.Thumb>
        <Thumb Width='13' Height='18' Focusable='False'>
          <Thumb.Template>
            <ControlTemplate TargetType='{x:Type Thumb}'>
              <Border x:Name='ThumbBorder' CornerRadius='2' BorderThickness='1'
                      Background='{DynamicResource EnvironmentScrollBarThumbBackground}'
                      BorderBrush='{DynamicResource CommonControlsTextBoxBorderFocused}' />
              <ControlTemplate.Triggers>
                <Trigger Property='IsMouseOver' Value='True'>
                  <Setter TargetName='ThumbBorder' Property='Background'
                          Value='{DynamicResource EnvironmentScrollBarThumbMouseOverBackground}' />
                  <Setter TargetName='ThumbBorder' Property='BorderBrush'
                          Value='{DynamicResource ToolBarButtonHoverBorder}' />
                </Trigger>
                <Trigger Property='IsDragging' Value='True'>
                  <Setter TargetName='ThumbBorder' Property='Background'
                          Value='{DynamicResource EnvironmentScrollBarThumbPressedBackground}' />
                </Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
          </Thumb.Template>
        </Thumb>
      </Track.Thumb>
    </Track>
  </Grid>
  <ControlTemplate.Triggers>
    <Trigger Property='IsEnabled' Value='False'>
      <Setter TargetName='PART_Track' Property='Opacity' Value='0.55' />
    </Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>");

		static TextBlock ValueText() => new TextBlock { Width = 70, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

		sealed class LineChoice {
			public int Value { get; }
			readonly string text;
			public LineChoice(int value, string text) { Value = value; this.text = text; }
			public override string ToString() => text;
		}

		sealed class KeyChoice {
			public Key Value { get; }
			readonly string text;

			public KeyChoice(Key value) {
				Value = value;
				text = DisplayName(value);
			}

			public override string ToString() => text;

			public static IEnumerable<KeyChoice> CreateChoices(Key selected) {
				var keys = new List<Key>();
				for (int value = (int)Key.A; value <= (int)Key.Z; value++)
					keys.Add((Key)value);
				for (int value = (int)Key.D0; value <= (int)Key.D9; value++)
					keys.Add((Key)value);
				for (int value = (int)Key.F1; value <= (int)Key.F12; value++)
					keys.Add((Key)value);
				keys.AddRange(new[] { Key.Insert, Key.Delete, Key.Home, Key.End, Key.PageUp, Key.PageDown });
				if (!keys.Contains(selected))
					keys.Add(selected);
				foreach (var key in keys)
					yield return new KeyChoice(key);
			}

			public static string DisplayName(Key key) {
				return CallGraphSettings.DisplayKeyName(key);
			}
		}
	}
}
