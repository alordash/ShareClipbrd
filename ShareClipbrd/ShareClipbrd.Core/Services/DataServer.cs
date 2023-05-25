﻿using System.Buffers;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Sockets;
using GuardNet;
using ShareClipbrd.Core.Clipboard;
using ShareClipbrd.Core.Configuration;
using ShareClipbrd.Core.Extensions;
using ShareClipbrd.Core.Helpers;

namespace ShareClipbrd.Core.Services {
    public interface IDataServer {
        void Start(Action<ClipboardData> onReceiveDataCb, Action<StringCollection> onReceiveFilesCb);
        void Stop();
    }

    public class DataServer : IDataServer {
        readonly ISystemConfiguration systemConfiguration;
        readonly IDialogService dialogService;
        readonly CancellationTokenSource cts;

        public DataServer(
            ISystemConfiguration systemConfiguration,
            IDialogService dialogService
            ) {
            Guard.NotNull(systemConfiguration, nameof(systemConfiguration));
            Guard.NotNull(dialogService, nameof(dialogService));
            this.systemConfiguration = systemConfiguration;
            this.dialogService = dialogService;

            cts = new CancellationTokenSource();
        }

        static async ValueTask<string> HandleFile(NetworkStream stream, int dataSize, Lazy<string> sessionDir, CancellationToken cancellationToken) {
            var filename = await stream.ReadUTF8StringAsync(cancellationToken);
            if(string.IsNullOrEmpty(filename)) {
                throw new NotSupportedException("Filename receive error");
            }
            await stream.WriteAsync(CommunProtocol.SuccessFilename, cancellationToken);

            var tempFilename = Path.Combine(sessionDir.Value, filename);
            var directory = Path.GetDirectoryName(tempFilename);
            if(!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) {
                Directory.CreateDirectory(directory);
            }
            using(var fileStream = new FileStream(tempFilename, FileMode.Create)) {
                byte[] receiveBuffer = ArrayPool<byte>.Shared.Rent(CommunProtocol.ChunkSize);
                while(fileStream.Length < dataSize) {
                    int receivedBytes = await stream.ReadAsync(receiveBuffer, cancellationToken);
                    if(receivedBytes == 0) {
                        break;
                    }
                    await fileStream.WriteAsync(new ReadOnlyMemory<byte>(receiveBuffer, 0, receivedBytes), cancellationToken);
                }
                await stream.WriteAsync(CommunProtocol.SuccessData, cancellationToken);
            }
            return tempFilename;
        }

        static string[] HandleZipArchive(NetworkStream stream, int itemsCount, Lazy<string> sessionDir, CancellationToken cancellationToken) {
            var files = new List<string>();

            using(var archive = new ZipArchive(stream, ZipArchiveMode.Read)) {
                Debug.WriteLine($"  Count: {archive.Entries.Count}");

                foreach(var entry in archive.Entries) {
                    var fileAttributes = (FileAttributes)entry.ExternalAttributes;
                    if(fileAttributes.HasFlag(FileAttributes.Directory)) {
                        var tempDirectory = Path.Combine(sessionDir.Value, entry.FullName);
                        Directory.CreateDirectory(tempDirectory);
                        files.Add(tempDirectory);
                    } else {
                        var tempFilename = Path.Combine(sessionDir.Value, entry.FullName);
                        var directory = Path.GetDirectoryName(tempFilename);
                        if(!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) {
                            Directory.CreateDirectory(directory);
                        }
                        entry.ExtractToFile(tempFilename, true);
                        files.Add(tempFilename);
                    }
                }
            }
            return files.ToArray();
        }

        static async ValueTask<MemoryStream> HandleData(NetworkStream stream, int dataSize, CancellationToken cancellationToken) {
            var memoryStream = new MemoryStream(dataSize);
            byte[] receiveBuffer = ArrayPool<byte>.Shared.Rent(CommunProtocol.ChunkSize);
            while(memoryStream.Length < dataSize) {
                int receivedBytes = await stream.ReadAsync(receiveBuffer, cancellationToken);
                if(receivedBytes == 0) {
                    break;
                }
                await memoryStream.WriteAsync(new ReadOnlyMemory<byte>(receiveBuffer, 0, receivedBytes), cancellationToken);
            }

            await stream.WriteAsync(CommunProtocol.SuccessData, cancellationToken);
            return memoryStream;
        }

        static string RecreateTempDirectory() {
            const string path = "ShareClipbrd_60D54950";
            var tempDir = Path.Combine(Path.GetTempPath(), path);
            if(Directory.Exists(tempDir)) {
                try {
                    Directory.Delete(tempDir, true);
                } catch { }
            }
            Directory.CreateDirectory(tempDir);
            return tempDir;
        }

        async ValueTask<string?> ReceiveFormat(NetworkStream stream, CancellationToken cancellationToken) {
            var format = await stream.ReadUTF8StringAsync(cancellationToken);
            if(string.IsNullOrEmpty(format)) {
                return null;
            }
            await stream.WriteAsync(CommunProtocol.SuccessFormat, cancellationToken);
            return format;
        }

        async ValueTask<Int64> ReceiveSize(NetworkStream stream, CancellationToken cancellationToken) {
            var size = await stream.ReadInt64Async(cancellationToken);
            await stream.WriteAsync(CommunProtocol.SuccessSize, cancellationToken);
            return size;
        }

        async ValueTask HandleClient(TcpClient tcpClient, Action<ClipboardData> onReceiveDataCb, Action<StringCollection> onReceiveFilesCb, CancellationToken cancellationToken) {
            var clipboardData = new ClipboardData();

            var sessionDir = new Lazy<string>(RecreateTempDirectory);

            try {
                var stream = tcpClient.GetStream();

                if(await stream.ReadUInt16Async(cancellationToken) != CommunProtocol.Version) {
                    await stream.WriteAsync(CommunProtocol.Error, cancellationToken);
                    throw new NotSupportedException("Wrong version of the other side");
                }
                await stream.WriteAsync(CommunProtocol.SuccessVersion, cancellationToken);

                var format = await ReceiveFormat(stream, cancellationToken);
                if(format == ClipboardData.Format.ZipArchive) {
                    var filesCount = await ReceiveSize(stream, cancellationToken);
                    var fileDropList = new StringCollection();
                    fileDropList.AddRange(HandleZipArchive(stream, (int)filesCount, sessionDir, cancellationToken));
                    onReceiveFilesCb(fileDropList);
                } else if(format == ClipboardData.Format.Bitmap) {

                } else if(format == ClipboardData.Format.WaveAudio) {

                } else {
                    while(!string.IsNullOrEmpty(format) && !cancellationToken.IsCancellationRequested) {
                        var size = await ReceiveSize(stream, cancellationToken);
                        clipboardData.Add(format, await HandleData(stream, (int)size, cancellationToken));
                        format = await ReceiveFormat(stream, cancellationToken);
                    }
                    onReceiveDataCb(clipboardData);
                }

                Debug.WriteLine($"tcpServer success finished");

            } catch(OperationCanceledException ex) {
                Debug.WriteLine($"tcpServer canceled {ex}");
            } catch(Exception ex) {
                dialogService.ShowError(ex);
            }
        }

        public void Start(Action<ClipboardData> onReceiveDataCb, Action<StringCollection> onReceiveFilesCb) {
            var cancellationToken = cts.Token;
            Task.Run(async () => {

                while(!cancellationToken.IsCancellationRequested) {
                    var adr = NetworkHelper.ResolveHostName(systemConfiguration.HostAddress);
                    var tcpServer = new TcpListener(adr.Address, adr.Port);
                    try {
                        Debug.WriteLine($"start tcpServer: {adr}");
                        tcpServer.Start();

                        while(!cancellationToken.IsCancellationRequested) {
                            using var tcpClient = await tcpServer.AcceptTcpClientAsync(cancellationToken);
                            Debug.WriteLine($"tcpServer accept  {tcpClient.Client.RemoteEndPoint}");

                            await HandleClient(tcpClient, onReceiveDataCb, onReceiveFilesCb, cancellationToken);
                        }
                    } catch(OperationCanceledException ex) {
                        Debug.WriteLine($"tcpServer canceled {ex}");
                    } catch(Exception ex) {
                        dialogService.ShowError(ex);
                    }

                    Debug.WriteLine($"tcpServer stop");
                    tcpServer.Stop();
                }

                Debug.WriteLine($"tcpServer stopped");
            }, cancellationToken);
        }

        public void Stop() {
            Debug.WriteLine($"tcpServer request to stop");
            cts.Cancel();
        }
    }
}
