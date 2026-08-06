using Caliburn.Micro;
using LocalEnvManager.Messages;
using LocalEnvManager.Models;
using LocalEnvManager.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LocalEnvManager.ViewModels
{
    /// <summary>
    /// Right-pane child view-model for non-uvicorn projects. Wraps a
    /// <see cref="ProjectLogsWatcher"/> to tail the most recently modified
    /// file in <c>&lt;TargetPath&gt;\..\logs\</c>. The dark console is shown
    /// when the directory exists, otherwise a hint explains what's missing.
    /// </summary>
    public sealed class LogFileLiveLogViewModel : Screen, ILiveLogViewModel,
        IHandle<LogFileChangedMessage>
    {
        private readonly IEventAggregator _events;
        private readonly ProjectLogsWatcher _watcher;
        private readonly BindableCollection<string> _lines = new();
        private readonly Func<ManagedProject?> _projectResolver;
        private readonly object _bufferGate = new();
        private readonly List<string> _pending = new();
        private System.Threading.Timer? _flushTimer;
        private int _projectId;

        public LogFileLiveLogViewModel(
            int projectId,
            IEventAggregator events,
            ProjectLogsWatcher watcher,
            Func<ManagedProject?> projectResolver)
        {
            _projectId = projectId;
            _events = events;
            _watcher = watcher;
            _projectResolver = projectResolver;
            _events.SubscribeOnUI(this);
        }

        public int ProjectId
        {
            get => _projectId;
            private set
            {
                if (_projectId == value) return;
                _projectId = value;
                NotifyOfPropertyChange();
            }
        }
        public LiveLogKind Kind => LiveLogKind.LogFile;
        public IEnumerable<string> Lines => _lines;
        private string _fileName = string.Empty;
        public string FileName
        {
            get => _fileName;
            private set
            {
                if (_fileName == value) return;
                _fileName = value;
                NotifyOfPropertyChange();
            }
        }

        private bool _available;
        public bool Available
        {
            get => _available;
            private set
            {
                if (_available == value) return;
                _available = value;
                NotifyOfPropertyChange();
            }
        }

        private string _hint = string.Empty;
        public string Hint
        {
            get => _hint;
            private set
            {
                if (_hint == value) return;
                _hint = value;
                NotifyOfPropertyChange();
            }
        }

        /// <summary>
        /// Called by the parent right after construction to kick off the
        /// underlying FileSystemWatcher and seed the buffer with the most
        /// recent 4 KiB of the freshest log file.
        /// </summary>
        public void Initialize()
        {
            // FileSystemWatcher and the polling fallback can report at the
            // same time. Do not let BindableCollection marshal individual
            // adds itself: it captures an insertion index before dispatching,
            // so concurrent reports can reach the UI with stale indexes.
            // Buffer on worker threads and drain one batch on the dispatcher.
            _watcher.SetSink(new Progress<string>(line =>
            {
                lock (_bufferGate) _pending.Add(line);
            }));
            _flushTimer ??= new System.Threading.Timer(
                _ => FlushPending(), null, dueTime: 50, period: 50);
            _watcher.TrackProject(_projectResolver());

            var dir = _watcher.LogsDirectory;
            DispatchToUi(() => SetInitialStatus(dir));
        }

        private void SetInitialStatus(string? dir)
        {
            if (string.IsNullOrEmpty(dir))
            {
                Available = false;
                FileName = string.Empty;
                Hint = "此项目未配置源路径或目标路径，无法定位 logs 文件夹。\n\n进程的标准输出仍可在「运行日志」标签页查看。";
            }
            else if (!Directory.Exists(dir))
            {
                Available = false;
                FileName = dir;
                Hint = $"尚未发现 logs 文件夹：\n{dir}\n\n项目首次运行后会自动开始 tail 此目录。\n进程的标准输出仍可在「运行日志」标签页查看。";
            }
            else
            {
                Available = true;
                FileName = dir;
                Hint = string.Empty;
            }
        }

        private void FlushPending()
        {
            string[] lines;
            lock (_bufferGate)
            {
                if (_pending.Count == 0) return;
                lines = _pending.ToArray();
                _pending.Clear();
            }

            DispatchToUi(() => AppendToBound(lines));
        }

        private void DispatchToUi(System.Action action)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                action();
            }
            else if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
            {
                _ = dispatcher.BeginInvoke(action);
            }
        }

        private void AppendToBound(IEnumerable<string> lines)
        {
            foreach (var line in lines)
            {
                _lines.Add(line);
            }

            var overflow = _lines.Count - ConsoleLiveLogViewModel.MaxLines;
            if (overflow > 0)
            {
                var drop = new List<string>(overflow);
                for (var i = 0; i < overflow; i++) drop.Add(_lines[i]);
                _lines.RemoveRange(drop);
            }

            Available = true;
            var current = _watcher.CurrentFile;
            if (!string.IsNullOrEmpty(current))
            {
                var name = Path.GetFileName(current);
                if (name != FileName)
                {
                    FileName = name;
                    _events.PublishOnCurrentThread(new LogFileChangedMessage(_projectId, name));
                }
            }
        }

        public Task HandleAsync(LogFileChangedMessage message, CancellationToken cancellationToken)
        {
            if (message.ProjectId != ProjectId) return Task.CompletedTask;
            FileName = message.FileName;
            return Task.CompletedTask;
        }

        protected override System.Threading.Tasks.Task OnDeactivateAsync(bool close, System.Threading.CancellationToken cancellationToken)
        {
            if (close)
            {
                try { _events.Unsubscribe(this); } catch { }
                _flushTimer?.Dispose();
                _flushTimer = null;
                try { _watcher.Dispose(); } catch { }
            }
            return base.OnDeactivateAsync(close, cancellationToken);
        }
    }
}
