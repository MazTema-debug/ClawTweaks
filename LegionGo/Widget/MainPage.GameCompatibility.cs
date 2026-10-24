// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LegionTweaks.Widget
{
    public sealed partial class MainPage
    {
        private const string GameCompatibilityKey = "GameCompatibility.v1";
        private static readonly object GameCatalogGate = new object();
        private GameCompatibilityCatalog gameCatalog;
        private string gameCatalogRevision, gameEditingId, gameExecutable;
        private bool gameEditorBusy, gameCatalogWritable;
        private void InitializeGameCompatibility()
        {
            try {
                object stored = ApplicationData.Current.LocalSettings.Values[GameCompatibilityKey];
                if (stored != null && !(stored is string)) throw new InvalidOperationException("El formato guardado no es válido.");
                gameCatalogRevision = stored as string;
                gameCatalog = gameCatalogRevision == null ? new GameCompatibilityCatalog() : Protocol.Decode<GameCompatibilityCatalog>(gameCatalogRevision);
                gameCatalog.Validate(); gameCatalogWritable = true;
                RefreshGameCompatibility();
            }
            catch (Exception ex) {
                gameCatalogWritable = false;
                GameAccessText.Text = "No se pudieron leer los accesos guardados. Se conserva la configuración: " + ex.Message;
                GameAccessSave.IsEnabled = GameAccessExport.IsEnabled = false;
            }
        }
        private void RefreshGameCompatibility(string selected = null)
        {
            GameAccessList.ItemsSource = null; GameAccessList.ItemsSource = gameCatalog.Games;
            GameAccessList.SelectedItem = gameCatalog.Games.FirstOrDefault(x => x.Id == selected);
            if (GameAccessList.SelectedItem == null) EditGameCompatibility(null);
        }
        private void EditGameCompatibility(GameCompatibilityEntry game)
        {
            gameEditingId = game?.Id ?? Guid.NewGuid().ToString("D");
            gameExecutable = game?.Executable;
            GameAccessName.Text = game?.Name ?? "";
            GameAccessPath.Text = gameExecutable ?? "Selecciona el ejecutable del juego.";
            GameAccessRawInput.IsOn = game?.DisableSdlRawInput == true;
            GameAccessExport.IsEnabled = game != null && gameCatalogWritable;
            GameAccessText.Text = game == null ? "Nuevo acceso. Seleccionar y guardar no abre el juego." : "Configuración guardada. Genera de nuevo el acceso si cambias alguna opción.";
        }
        private void GameAccessSelection_Changed(object sender, SelectionChangedEventArgs e)
        { if (gameCatalog != null && !gameEditorBusy) EditGameCompatibility(GameAccessList.SelectedItem as GameCompatibilityEntry); }
        private void GameAccessNew_Click(object sender, RoutedEventArgs e)
        { if (!gameEditorBusy && gameCatalogWritable) { GameAccessList.SelectedItem = null; EditGameCompatibility(null); } }
        private async void GameAccessPick_Click(object sender, RoutedEventArgs e)
        {
            if (gameEditorBusy || !gameCatalogWritable) return;
            if (widget != null) { GameAccessText.Text = "Selecciona el ejecutable desde la app de escritorio de LegionTweaks."; return; }
            gameEditorBusy = true;
            try {
                var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
                picker.FileTypeFilter.Add(".exe");
                var file = await picker.PickSingleFileAsync();
                if (file == null) return;
                var draft = new GameCompatibilityEntry { Id = gameEditingId, Name = "Juego", Executable = file.Path };
                draft.Validate(); gameExecutable = file.Path; GameAccessPath.Text = file.Path;
                if (string.IsNullOrWhiteSpace(GameAccessName.Text)) GameAccessName.Text = file.DisplayName;
                GameAccessText.Text = "Ejecutable seleccionado. Guarda la configuración antes de generar el acceso.";
            }
            catch (Exception ex) { GameAccessText.Text = "No se seleccionó el juego: " + ex.Message; }
            finally { gameEditorBusy = false; }
        }
        private GameCompatibilityEntry GameCompatibilityDraft() => new GameCompatibilityEntry {
            Id = gameEditingId, Name = GameAccessName.Text.Trim(), Executable = gameExecutable, DisableSdlRawInput = GameAccessRawInput.IsOn };
        private void GameAccessSave_Click(object sender, RoutedEventArgs e)
        {
            if (gameEditorBusy || !gameCatalogWritable) return;
            try {
                var draft = GameCompatibilityDraft(); draft.Validate();
                lock (GameCatalogGate) {
                    string actual = ApplicationData.Current.LocalSettings.Values[GameCompatibilityKey] as string;
                    if (!string.Equals(actual, gameCatalogRevision, StringComparison.Ordinal))
                        throw new InvalidOperationException("Los accesos cambiaron en otra ventana. Cierra y vuelve a abrir este panel para recargarlos.");
                    var next = gameCatalog.Copy();
                    int index = next.Games.FindIndex(x => x.Id == draft.Id);
                    if (index < 0) next.Games.Add(draft); else next.Games[index] = draft;
                    next.Validate(); string json = Protocol.Encode(next);
                    if (Encoding.Unicode.GetByteCount(json) > 7000) throw new InvalidOperationException("La lista de accesos es demasiado grande para guardar otra entrada.");
                    try {
                        ApplicationData.Current.LocalSettings.Values[GameCompatibilityKey] = json;
                        if (!string.Equals(ApplicationData.Current.LocalSettings.Values[GameCompatibilityKey] as string, json, StringComparison.Ordinal))
                            throw new InvalidOperationException("No se pudo verificar el guardado. Vuelve a abrir este panel.");
                    }
                    catch {
                        // Restore only our own tentative write, never overwrite
                        // an unexpected concurrent value from another writer.
                        try {
                            if (string.Equals(ApplicationData.Current.LocalSettings.Values[GameCompatibilityKey] as string, json, StringComparison.Ordinal)) {
                                if (actual == null) ApplicationData.Current.LocalSettings.Values.Remove(GameCompatibilityKey);
                                else ApplicationData.Current.LocalSettings.Values[GameCompatibilityKey] = actual;
                            }
                        } catch (Exception) { /* Retain the failed outcome; require a fresh load. */ }
                        throw;
                    }
                    gameCatalog = next; gameCatalogRevision = json;
                }
                RefreshGameCompatibility(draft.Id);
                GameAccessText.Text = "Configuración guardada. No se abrió el juego. Genera el acceso compatible para utilizarla.";
            }
            catch (Exception ex) { GameAccessText.Text = "No se guardó el acceso: " + ex.Message; }
        }
        private async void GameAccessExport_Click(object sender, RoutedEventArgs e)
        {
            if (gameEditorBusy || !gameCatalogWritable) return;
            if (widget != null) { GameAccessText.Text = "Genera el acceso desde la app de escritorio de LegionTweaks."; return; }
            gameEditorBusy = true;
            try {
                var saved = gameCatalog.Games.FirstOrDefault(x => x.Id == gameEditingId)?.Copy();
                if (saved == null || !string.Equals(Protocol.Encode(saved), Protocol.Encode(GameCompatibilityDraft()), StringComparison.Ordinal))
                    throw new InvalidOperationException("Guarda primero los cambios del juego.");
                string actual = ApplicationData.Current.LocalSettings.Values[GameCompatibilityKey] as string;
                if (!string.Equals(actual, gameCatalogRevision, StringComparison.Ordinal))
                    throw new InvalidOperationException("Los accesos cambiaron en otra ventana. Vuelve a abrir este panel.");
                string access = GameCompatibilityAccess.Create(saved);
                var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.Desktop, SuggestedFileName = GameCompatibilityAccess.SuggestedName(saved.Name) };
                picker.FileTypeChoices.Add("Acceso compatible de Windows", new List<string> { ".cmd" });
                var file = await picker.PickSaveFileAsync();
                if (file == null) return;
                // UTF-8 without a BOM; the ASCII preamble selects codepage 65001
                // before cmd reads a path containing non-ASCII characters.
                await FileIO.WriteBytesAsync(file, new UTF8Encoding(false).GetBytes(access));
                var written = await FileIO.ReadBufferAsync(file);
                using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(written)) {
                    var bytes = new byte[written.Length]; reader.ReadBytes(bytes);
                    if (!string.Equals(new UTF8Encoding(false, true).GetString(bytes), access, StringComparison.Ordinal))
                        throw new InvalidOperationException("No se pudo verificar el archivo generado.");
                }
                GameAccessText.Text = "Acceso compatible guardado y verificado. Ábrelo para iniciar «" + saved.Name + "» con esta configuración. Los accesos anteriores no se actualizan solos.";
            }
            catch (Exception ex) { GameAccessText.Text = "No se generó el acceso: " + ex.Message; }
            finally { gameEditorBusy = false; }
        }
    }
}
