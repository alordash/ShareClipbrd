﻿using System.Collections.Specialized;
using ShareClipbrd.Core.Clipboard;

namespace ShareClipbrd.Core.Services {
    public interface IDispatchService {
        void ReceiveData(ClipboardData clipboardData);
        void ReceiveFiles(StringCollection files);
    }
}
