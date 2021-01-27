﻿using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Engine.Application;
using ICSharpCode.AvalonEdit;
using MaterialDesignExtensions.Controls;
using TextrudeInteractive.Annotations;
using TextrudeInteractive.AutoCompletion;

namespace TextrudeInteractive
{
	/// <summary>
	///     Interaction logic for MainWindow.xaml
	/// </summary>
	public partial class MainWindow : MaterialWindow, INotifyPropertyChanged
	{
		private readonly AvalonEditCompletionHelper _mainEditWindow;
		private readonly ProjectManager _projectManager;
		private readonly bool _uiIsReady;

		private readonly ComboBox[] formats;

		private readonly ISubject<EngineInputSet> InputStream =
			new BehaviorSubject<EngineInputSet>(EngineInputSet.EmptyYaml);

		private readonly TextEditor[] modelBoxes;
		private readonly TextEditor[] OutputBoxes;
		private UpgradeManager.VersionInfo _latestVersion = UpgradeManager.VersionInfo.Default;

		private bool _lineNumbersOn = true;

		private double _textSize = 14;

		private bool _wordWrapOn;

		private Microsoft.Web.WebView2.Core.CoreWebView2Environment _webEnv;

		public MainWindow()
		{
			InitializeComponent();
			SetTitle(string.Empty);
			formats = new[] { format0, format1, format2 };
			modelBoxes = new[] { ModelTextBox0, ModelTextBox1, ModelTextBox2 };
			OutputBoxes = new[] {/*OutputText0,*/ OutputText1, OutputText2 };

			_mainEditWindow = new AvalonEditCompletionHelper(TemplateTextBox);
			InitWebView();

			_projectManager = new ProjectManager(this);
			foreach (var comboBox in formats)
			{
				comboBox.ItemsSource = Enum.GetValues(typeof(ModelFormat));
			}

			using (var zipStream = new MemoryStream(Properties.Resources.monaco_editor_0_21_2))
			{
				using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Read))
				{
					var monacoLangRegex = new System.Text.RegularExpressions.Regex(@"vs/basic-languages/(?<name>.+)/$");
					var monacoLangs = zip.Entries
						.Select(e => monacoLangRegex.Match(e.FullName))
						.Where(m => m.Success)
						.Select(m => m.Groups["name"].Value)
						.ToList();
					outpuFormat.ItemsSource = monacoLangs;
					outpuFormat.SelectedValue = "html";
				}
			}

			SetUI(EngineInputSet.EmptyYaml);

			InputStream
				.Throttle(TimeSpan.FromMilliseconds(300))
				.ObserveOn(NewThreadScheduler.Default)
				.Select(Render)
				.ObserveOn(SynchronizationContext.Current)
				.Subscribe(HandleRenderResults);
			_uiIsReady = true;
			RunBackgroundUpgradeCheck();
			DataContext = this;
		}

		private async void InitWebView()
		{
			_webEnv = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync();
			await webView.EnsureCoreWebView2Async(_webEnv);

			webView.CoreWebView2.AddWebResourceRequestedFilter("http://*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All);
			webView.CoreWebView2.WebResourceRequested += CoreWebView2_WebResourceRequested; ;

			webView.CoreWebView2.Navigate("http://monaco-editor"); // TODO Magic string -> replace with constant
#if DEBUG
			webView.CoreWebView2.OpenDevToolsWindow();
#endif
		}

		private void CoreWebView2_WebResourceRequested(object sender, Microsoft.Web.WebView2.Core.CoreWebView2WebResourceRequestedEventArgs e)
		{
			if (e.Request.Uri == "http://monaco-editor/")
			{
				e.Response = _webEnv.CreateWebResourceResponse(
					new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Properties.Resources.monaco)),
					200,
					"OK",
					""
				);
			}
			else if (e.Request.Uri.StartsWith("http://monaco-editor/vs"))
			{
				var path = e.Request.Uri.Replace("http://monaco-editor/", "");
				using (var zipStream = new MemoryStream(Properties.Resources.monaco_editor_0_21_2))
				{
					using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Read))
					{
						var file = zip.GetEntry(path);
						var response = new MemoryStream(); // cache into local stream so is not disposed
						file.Open().CopyTo(response);
						response.Position = 0;

						string mimeType;
						switch (Path.GetExtension(path))
						{
							case ".js":
								mimeType = "text/javascript";
								break;
							case ".css":
								mimeType = "text/css";
								break;
							case ".ttf":
								mimeType = "font/ttf";
								break;
							default:
								mimeType = "application/octet-stream";
								break;
						}

						e.Response = _webEnv.CreateWebResourceResponse(
							response,
							200,
							"OK",
							$"Content-Type: {mimeType}\nContent-Length: {response.Length}"
						);
					}
				}
			}
		}

		public bool LineNumbersOn
		{
			get => _lineNumbersOn;
			set
			{
				_lineNumbersOn = value;
				OnPropertyChanged();
			}
		}

		public double TextSize
		{
			get => _textSize;
			set
			{
				_textSize = value;
				OnPropertyChanged();
			}
		}

		public bool WordWrapOn
		{
			get => _wordWrapOn;
			set
			{
				_wordWrapOn = value;
				OnPropertyChanged();
			}
		}

		public event PropertyChangedEventHandler? PropertyChanged;

		private void RunBackgroundUpgradeCheck()
		{
			Task.Run(async () =>
			{
				while (true)
				{
					_latestVersion = await UpgradeManager.GetLatestVersion();
					await Task.Delay(TimeSpan.FromHours(24));
				}
			});
		}

		public void SetTitle(string path)
		{
#if HASGITVERSION

			var file = Path.GetFileNameWithoutExtension(path);
			var title =
				$"Textrude Interactive {GitVersionInformation.SemVer} : {file}";
			Title = title;
#endif
		}

		private TimedOperation<ApplicationEngine> Render(EngineInputSet gi)
		{
			var rte = new RunTimeEnvironment(new FileSystemOperations());
			var engine = new ApplicationEngine(rte);
			var timer = new TimedOperation<ApplicationEngine>(engine);

			foreach (var m in gi.Models)
				engine = engine.WithModel(m.Text, m.Format);
			engine = engine
				.WithEnvironmentVariables()
				.WithDefinitions(gi.Definitions)
				.WithIncludePaths(gi.IncludePaths)
				.WithHelpers()
				.WithTemplate(gi.Template);

			engine.Render();
			return timer;
		}

		private void HandleRenderResults(TimedOperation<ApplicationEngine> timedEngine)
		{
			var elapsedMs = (int)timedEngine.Timer.ElapsedMilliseconds;
			var engine = timedEngine.Value;
			var outputs = engine.GetOutput(OutputBoxes.Length);
			for (var i = 0; i < outputs.Length; i++)
			{
				OutputBoxes[i].Text = outputs[i];
			}

			// TODO Implement for all outputs
			if (webView != null && webView.CoreWebView2 != null)
			{
				webView.CoreWebView2.PostWebMessageAsJson(
					new UpdateMonacoTextMessage(outputs[0], outpuFormat.SelectedValue.ToString()).ToJson()
				);
			}


			Errors.Text = string.Empty;
#if HASGITVERSION
			if (_latestVersion.Supersedes(GitVersionInformation.SemVer))
			{
				Errors.Text =
					$"Upgrade to {_latestVersion.Version} available - please visit {UpgradeManager.ReleaseSite}" +
					Environment.NewLine;
			}
#endif
			Errors.Text += $"Completed: {DateTime.Now.ToLongTimeString()}  Render time: {elapsedMs}ms" +
						   Environment.NewLine;
			if (engine.HasErrors)
			{
				Errors.Foreground = Brushes.OrangeRed;
				Errors.Text += string.Join(Environment.NewLine, engine.Errors);
			}
			else
			{
				Errors.Foreground = Brushes.GreenYellow;
				Errors.Text += "No errors";
			}

			_mainEditWindow.SetCompletion(engine.ModelPaths());
		}


		public EngineInputSet CollectInput()
		{
			var models = Enumerable.Range(0, formats.Length)
				.Select(i => new ModelText(modelBoxes[i].Text, (ModelFormat)formats[i].SelectedValue))
				.ToArray();

			return new EngineInputSet(TemplateTextBox.Text,
				models,
				DefinitionsTextBox.Text,
				IncludesTextBox.Text);
		}

		private void OnModelChanged()
		{
			if (!_uiIsReady)
				return;
			try
			{
				InputStream.OnNext(CollectInput());
			}
			catch (Exception exception)
			{
				Errors.Text = exception.Message;
			}
		}

		private void OnModelFormatChanged(object sender, SelectionChangedEventArgs e)
		{
			OnModelChanged();
		}

		private void OnOutputFormatChanged(object sender, SelectionChangedEventArgs e)
		{
			if (webView != null && webView.CoreWebView2 != null)
			{
				webView.CoreWebView2.PostWebMessageAsJson(
					new UpdateMonacoLanguageMessage(outpuFormat.SelectedValue.ToString()).ToJson()
				);
			}
		}

		private void LoadProject(object sender, RoutedEventArgs e)
		{
			_projectManager.LoadProject();
		}


		private void SaveProject(object sender, RoutedEventArgs e)
		{
			_projectManager.SaveProject();
		}


		private void SaveProjectAs(object sender, RoutedEventArgs e)
		{
			_projectManager.SaveProjectAs();
		}


		private void MainWindow_OnClosing(object sender, CancelEventArgs e)
		{
			InputStream.OnCompleted();
		}


		public void SetUI(EngineInputSet gi)
		{
			DefinitionsTextBox.Text = string.Join(Environment.NewLine, gi.Definitions);

			for (var i = 0; i < formats.Length; i++)
			{
				var model = (i < gi.Models.Length) ? gi.Models[i] : ModelText.EmptyYaml;
				formats[i].SelectedValue = model.Format;
				modelBoxes[i].Text = model.Text;
			}

			TemplateTextBox.Text = gi.Template;

			IncludesTextBox.Text = string.Join(Environment.NewLine, gi.IncludePaths);
		}

		private void OpenBrowserTo(Uri uri)
		{
			var ps = new ProcessStartInfo(uri.AbsoluteUri)
			{
				UseShellExecute = true,
				Verb = "open"
			};
			Process.Start(ps);
		}


		private void ShowAbout(object sender, RoutedEventArgs e)
		{
			OpenBrowserTo(new Uri("https://github.com/NeilMacMullen/Textrude"));
		}

		private void ShowLanguageRef(object sender, RoutedEventArgs e)
		{
			OpenBrowserTo(new Uri("https://github.com/scriban/scriban/blob/master/doc/language.md"));
		}

		private void NewProject(object sender, RoutedEventArgs e)
		{
			_projectManager.NewProject();
		}

		private void NewIssue(object sender, RoutedEventArgs e)
		{
			OpenBrowserTo(new Uri("https://github.com/NeilMacMullen/Textrude/issues/new"));
		}

		private void ExportInvocation(object sender, RoutedEventArgs e)
		{
			_projectManager.ExportProject();
		}

		private void Avalon1_OnTextChanged(object sender, EventArgs e)
		{
			OnModelChanged();
		}

		private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
		{
			_mainEditWindow.Register();
		}

		[NotifyPropertyChangedInvocator]
		protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
		{
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		}

		private void SmallerFont(object sender, RoutedEventArgs e)
		{
			TextSize = Math.Max(TextSize - 2, 10);
		}

		private void LargerFont(object sender, RoutedEventArgs e)
		{
			TextSize = Math.Min(TextSize + 2, 36);
		}

		private void ToggleLineNumbers(object sender, RoutedEventArgs e)
		{
			LineNumbersOn = !LineNumbersOn;
		}

		private void ToggleWordWrap(object sender, RoutedEventArgs e)
		{
			WordWrapOn = !WordWrapOn;
		}

		private record UpdateMonacoTextMessage
		{
			public UpdateMonacoTextMessage(string text, string language)
			{
				Text = text;
				Language = language;
			}

			[JsonPropertyName("type")]
			public string Type { get; } = "UpdateText";
			[JsonPropertyName("text")]
			public string Text { get; }
			[JsonPropertyName("language")]
			public string Language { get; }

			public string ToJson() => JsonSerializer.Serialize(this);
		}

		private record UpdateMonacoLanguageMessage
		{
			public UpdateMonacoLanguageMessage(string language)
			{
				Language = language;
			}

			[JsonPropertyName("type")]
			public string Type { get; } = "UpdateLanguage";
			[JsonPropertyName("language")]
			public string Language { get; }

			public string ToJson() => JsonSerializer.Serialize(this);
		}
	}
}