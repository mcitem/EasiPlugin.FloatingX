using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using Cvte.Composition;
using Cvte.EasiNote;
using Cvte.EasiNote.Business.Courseware.Cloud;
using Cvte.EasiNote.Document;
using Cvte.EasiNote.Storage;

namespace FloatingX;

internal sealed class BlankWhiteboardSyncGuard
{
    private readonly IDocumentProvider _document;
    private readonly IEnbxStorageProvider _storage;
    private readonly object _switch;
    private readonly PropertyInfo _forbidSync;
    private readonly string _temporaryRoot;
    private string? _localDocumentId;
    private bool _protecting;
    private bool _previousForbidSync;

    private BlankWhiteboardSyncGuard(IDocumentProvider document, IEnbxStorageProvider storage)
    {
        _document = document;
        _storage = storage;
        var control = typeof(CloudCoursewareOpenProvider).Assembly
            .GetType("Cvte.EasiNote.Business.InnerFeatureEntryControl", throwOnError: true)!;
        _switch = control.GetField("Switch", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        _forbidSync = control.GetProperty("IsForbiddenCommitSyncCoursewareTask")!;
        _temporaryRoot = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator((string)EN.Path.TempFolder))!;
    }

    internal static async Task RegisterAsync()
    {
        var document = await Container.Current.GetAsync<IDocumentProvider>().ConfigureAwait(false);
        var storage = await Container.Current.GetAsync<IEnbxStorageProvider>().ConfigureAwait(false);
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var guard = new BlankWhiteboardSyncGuard(document, storage);
            document.DocumentChanged += guard.OnDocumentChanged;
            guard.Update();
        });
    }

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs args)
    {
        // Set the native gate before document-change processing can start a sync.
        Update();
    }

    private void Update()
    {
        bool isLocalWhiteboard = IsFloatingXWhiteboard(_storage.EnbxFilePath, _temporaryRoot);
        if (isLocalWhiteboard)
        {
            _localDocumentId = _document.DocumentId;
        }

        bool protect = isLocalWhiteboard ||
            (!string.IsNullOrEmpty(_localDocumentId) && _localDocumentId == _document.DocumentId);
        if (protect)
        {
            if (!_protecting)
            {
                _previousForbidSync = (bool)_forbidSync.GetValue(_switch)!;
                _protecting = true;
            }
            // This native switch is checked before submitting a cloud sync task.
            // Local storage and file export continue to use the host implementation.
            _forbidSync.SetValue(_switch, true);
        }
        else if (_protecting)
        {
            _forbidSync.SetValue(_switch, _previousForbidSync);
            _protecting = false;
            _localDocumentId = null;
        }
    }

    private static bool IsFloatingXWhiteboard(string? path, string temporaryRoot)
    {
        if (!string.Equals(Path.GetFileName(path), BlankWhiteboard.FileName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var session = new FileInfo(path!).Directory;
        // Cloud and Shell have distinct per-process folders beneath the same Temp root.
        return session != null &&
            Guid.TryParseExact(session.Name, "N", out _) &&
            string.Equals(session.Parent?.Name, "EasiPlugin.FloatingX", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(session.Parent?.Parent?.Parent?.FullName, temporaryRoot, StringComparison.OrdinalIgnoreCase);
    }
}
