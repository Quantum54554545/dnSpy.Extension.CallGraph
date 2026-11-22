using System;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Extension;
using dnSpy.Contracts.Menus;
using dnSpy.Contracts.Settings;

namespace dnSpy.CallGraph.Updates {
	[Export, PartCreationPolicy(CreationPolicy.Shared)]
	sealed class update_service {
		readonly IAppWindow app;
		readonly update_state state;
		readonly Func<update_client> create_client;
		readonly CancellationTokenSource lifetime = new();
		bool checking, installing, started;
		Window? notes;
		IInfoBarElement? notification;
		[ImportingConstructor]
		public update_service(IAppWindow app, ISettingsService settings) : this(app, new update_state(settings), () => new update_client()) { }
		internal update_service(IAppWindow app, update_state state, Func<update_client> create_client) {
			this.app = app; this.state = state; this.create_client = create_client; app.MainWindowClosed += (_, __) => this.lifetime.Cancel();
		}
		public void start() {
			if (this.started) return; this.started = true;
			try { update_installer.acknowledge_startup(Environment.GetEnvironmentVariable("CALLGRAPH_UPDATE_PLAN")); } catch (Exception) { }
			Environment.SetEnvironmentVariable("CALLGRAPH_UPDATE_PLAN", null);
			string? failure = Environment.GetEnvironmentVariable("CALLGRAPH_UPDATE_ERROR"); Environment.SetEnvironmentVariable("CALLGRAPH_UPDATE_ERROR", null);
			this.app.MainWindow.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => {
				if (this.lifetime.IsCancellationRequested) return;
				if (!string.IsNullOrEmpty(failure)) this.show_error(new IOException(failure));
				if (this.state.should_show_notes) this.show_notes();
				_ = this.check_async();
			}));
		}
		public void show_notes() {
			if (this.notes is not null) { this.notes.Activate(); return; }
			try {
				var window = release_notes_window.create(this.app.MainWindow); this.notes = window;
				window.Loaded += (_, __) => { try { this.state.mark_notes_seen(); } catch (Exception error) { this.show_error(error); } };
				window.Closed += (_, __) => this.notes = null; window.Show();
			}
			catch (Exception error) { this.show_error(error); }
		}
		internal async Task check_async() {
			if (this.checking || this.installing || this.lifetime.IsCancellationRequested) return; this.checking = true;
			try {
				using var client = this.create_client(); var release = await client.check_async(this.lifetime.Token);
				if (this.lifetime.IsCancellationRequested) return;
				if (!this.state.should_offer(release)) return;
				this.notification?.Close();
				this.notification = this.show_offer(release.display_version, context => { _ = this.install_async(release); }, context => { try { this.state.ignore(release); context.CloseElement(); } catch (Exception error) { this.show_error(error); } });
			}
			catch (Exception) { }
			finally { this.checking = false; }
		}
		IInfoBarElement show_offer(string version, Action<IInfoBarInteractionContext> download, Action<IInfoBarInteractionContext> ignore) =>
			this.app.InfoBar.Show(ui_text.pick("A new Call Graph version is available: ", "Доступна новая версия Call Graph: ") + version, InfoBarIcon.Information,
				new InfoBarInteraction(ui_text.pick("Download update", "Скачать обновление"), download),
				new InfoBarInteraction(ui_text.pick("Ignore this message", "Проигнорировать это сообщение"), ignore));
		async Task install_async(update_release release) {
			if (this.installing || this.lifetime.IsCancellationRequested) return;
			if (!release.is_dll) { this.show_error(new InvalidDataException(ui_text.pick("This release contains an archive. Publish a direct .dll asset for automatic installation; no files have been changed.", "Этот релиз содержит архив. Для автоустановки опубликуйте прямую ссылку на .dll; файлы не изменены."))); return; }
			if (!release_notes_window.confirm(this.app.MainWindow, release)) return;
			this.installing = true; prepared_update? prepared = null; Process? helper = null; IInfoBarElement? progress = null;
			try {
				this.notification?.Close(); progress = this.app.InfoBar.Show(ui_text.pick("Downloading and checking Call Graph…", "Скачивание и проверка Call Graph…"));
				using var client = this.create_client(); prepared = await update_installer.prepare_async(client, release, this.lifetime.Token);
				helper = Process.Start(update_installer.launcher(prepared.directory_path)) ?? throw new IOException("Could not start the update helper.");
				await update_installer.wait_until_ready_async(prepared, helper, this.lifetime.Token);
				progress.Close(); progress = null;
				this.app.MainWindow.Close();
				if (!this.lifetime.IsCancellationRequested) { prepared.cancel(); await helper.WaitForExitAsync(); this.show_info(ui_text.pick("Update cancelled: dnSpy is still open.", "Обновление отменено: dnSpy не был закрыт.")); }
				prepared = null;
			}
			catch (Exception error) {
				if (prepared is not null && helper is not null && !helper.HasExited) {
					try { prepared.cancel(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await helper.WaitForExitAsync(timeout.Token); } catch (Exception) { }
				}
				if (!this.lifetime.IsCancellationRequested) this.show_error(error);
			}
			finally { progress?.Close(); helper?.Dispose(); prepared?.Dispose(); this.installing = false; }
		}
		void show_info(string message) => this.app.InfoBar.Show(message, InfoBarIcon.Information);
		void show_error(Exception error) {
			string summary = error.Message.Replace('\r', ' ').Replace('\n', ' ').Trim(); if (summary.Length > 220) summary = summary.Substring(0, 220) + "…";
			this.app.InfoBar.Show(ui_text.pick("Call Graph update failed: ", "Не удалось обновить Call Graph: ") + summary, InfoBarIcon.Error);
		}
	}
	[ExportAutoLoaded]
	sealed class update_startup : IAutoLoaded {
		[ImportingConstructor]
		public update_startup(update_service service) => service.start();
	}
	[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_HELP_GUID, Header = "What's new in Call Graph", Group = MenuConstants.GROUP_APP_MENU_HELP_LINKS, Order = 2051)]
	sealed class call_graph_notes_menu : MenuItemBase {
		readonly update_service service;
		[ImportingConstructor]
		public call_graph_notes_menu(update_service service) => this.service = service;
		public override string? GetHeader(IMenuItemContext context) => ui_text.pick("What's new in Call Graph", "Что нового в Call Graph");
		public override void Execute(IMenuItemContext context) => this.service.show_notes();
	}
}
