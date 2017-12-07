﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using Windows.Networking.Connectivity;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using NextcloudApp.Converter;
using NextcloudApp.Models;
using NextcloudApp.Services;
using NextcloudApp.Utils;
using NextcloudClient.Exceptions;
using Prism.Commands;
using Prism.Windows.Navigation;
using NextcloudClient.Types;
using Prism.Windows.AppModel;

namespace NextcloudApp.ViewModels
{
    public class FileInfoPageViewModel : ViewModel
    {
        private readonly INavigationService _navigationService;
        private readonly IResourceLoader _resourceLoader;
        private readonly DialogService _dialogService;
        private DirectoryService _directoryService;
        private readonly TileService _tileService;
        private ResourceInfo _resourceInfo;
        private string _fileExtension;
        private string _fileName;
        private string _fileSizeString;
        private BitmapImage _thumbnail;
        public ICommand DownloadCommand { get; }
        public ICommand DeleteResourceCommand { get; }
        public ICommand RenameResourceCommand { get; }
        public ICommand MoveResourceCommand { get; }
        public ICommand PinToStartCommand { get; }

        public FileInfoPageViewModel(INavigationService navigationService, IResourceLoader resourceLoader, DialogService dialogService)
        {
            _navigationService = navigationService;
            _resourceLoader = resourceLoader;
            _dialogService = dialogService;

            Directory = DirectoryService.Instance;
            _tileService = TileService.Instance;

            //DataTransferManager dataTransferManager = DataTransferManager.GetForCurrentView();
            //dataTransferManager.DataRequested += new TypedEventHandler<DataTransferManager, DataRequestedEventArgs>(ShareImageHandler);
            DownloadCommand = new DelegateCommand(() =>
            {
                var parameters = new FileDownloadPageParameters
                {
                    ResourceInfo = ResourceInfo
                };

                _navigationService.Navigate(PageToken.FileDownload.ToString(), parameters.Serialize());
            });

            DeleteResourceCommand = new DelegateCommand(DeleteResource);
            RenameResourceCommand = new DelegateCommand(RenameResource);
            MoveResourceCommand = new RelayCommand(MoveResource);
            PinToStartCommand = new DelegateCommand<object>(PinToStart, CanPinToStart);
        }
        
        public override void OnNavigatedTo(NavigatedToEventArgs e, Dictionary<string, object> viewModelState)
        {
            base.OnNavigatedTo(e, viewModelState);

            var parameters = FileInfoPageParameters.Deserialize(e.Parameter);
            var resourceInfo = parameters?.ResourceInfo;

            if (resourceInfo == null)
            {
                return;
            }

            Directory.RebuildPathStackFromResourceInfo(resourceInfo);
            
            Directory.PathStack.Add(new PathInfo
            {
                ResourceInfo = resourceInfo
            });

            Directory.PathStack.CollectionChanged += PathStack_CollectionChanged;

            ResourceInfo = resourceInfo;
            FileExtension = Path.GetExtension(ResourceInfo.Name);
            FileName = Path.GetFileNameWithoutExtension(ResourceInfo.Name);
            var converter = new BytesToHumanReadableConverter();

            FileSizeString = LocalizationService.Instance.GetString(
                "FileSizeString",
                converter.Convert(ResourceInfo.Size, typeof(string), null, CultureInfo.CurrentCulture.ToString()),
                ResourceInfo.Size
            );

            DownloadPreviewImages();
        }

        private void PathStack_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            _navigationService.GoBack();
        }

        public override void OnNavigatingFrom(NavigatingFromEventArgs e, Dictionary<string, object> viewModelState, bool suspending)
        {
            Directory.PathStack.CollectionChanged -= PathStack_CollectionChanged;
            if (!suspending)
            {
                Directory.StopDirectoryListing();
                Directory = null;
            }
            base.OnNavigatingFrom(e, viewModelState, suspending);
        }

        private async void DownloadPreviewImages()
        {
            var client = await ClientService.GetClient();
            if (client == null)
            {
                return;
            }

            switch (SettingsService.Default.Value.LocalSettings.PreviewImageDownloadMode)
            {
                case PreviewImageDownloadMode.Always:
                    break;
                case PreviewImageDownloadMode.WiFiOnly:
                    var connectionProfile = NetworkInformation.GetInternetConnectionProfile();
                    // connectionProfile can be null (e.g. airplane mode)
                    if (connectionProfile == null || !connectionProfile.IsWlanConnectionProfile)
                    {
                        return;
                    }
                    break;
                case PreviewImageDownloadMode.Never:
                    return;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            try
            {
                Stream stream = null;
                try
                {
                    stream = await client.GetThumbnail(ResourceInfo, 300, 300);
                }
                catch (ResponseError e)
                {
                    ResponseErrorHandlerService.HandleException(e);
                }

                if (stream == null)
                {
                    return;
                }
                var bitmap = new BitmapImage();
                using (var memStream = new MemoryStream())
                {
                    await stream.CopyToAsync(memStream);
                    memStream.Position = 0;
                    bitmap.SetSource(memStream.AsRandomAccessStream());
                }
                Thumbnail = bitmap;
            }
            catch (ResponseError)
            {
                Thumbnail = new BitmapImage
                {
                    UriSource = new Uri("ms-appx:///Assets/Images/ThumbnailNotFound.png")
                };
            }
        }

        public BitmapImage Thumbnail
        {
            get => _thumbnail;
            set
            {
                if (_thumbnail == value)
                {
                    return;
                }
                _thumbnail = value;
                // ReSharper disable once ExplicitCallerInfoArgument
                RaisePropertyChanged(nameof(Thumbnail));
            }
        }

        public string FileSizeString
        {
            get => _fileSizeString;
            private set => SetProperty(ref _fileSizeString, value);
        }

        public string FileExtension
        {
            get => _fileExtension;
            private set => SetProperty(ref _fileExtension, value);
        }

        public string FileName
        {
            get => _fileName;
            private set => SetProperty(ref _fileName, value);
        }

        public ResourceInfo ResourceInfo
        {
            get => _resourceInfo;
            private set => SetProperty(ref _resourceInfo, value);
        }

        public DirectoryService Directory
        {
            get => _directoryService;
            private set => SetProperty(ref _directoryService, value);
        }

        private async void DeleteResource()
        {
            if (ResourceInfo == null)
            {
                return;
            }

            var dialog = new ContentDialog
            {
                Title = _resourceLoader.GetString(ResourceInfo.ContentType.Equals("dav/directory") ? "DeleteFolder" : "DeleteFile"),
                Content = new TextBlock
                {
                    Text = string.Format(_resourceLoader.GetString(ResourceInfo.ContentType.Equals("dav/directory") ? "DeleteFolder_Description" : "DeleteFile_Description"), ResourceInfo.Name),
                    TextWrapping = TextWrapping.WrapWholeWords,
                    Margin = new Thickness(0, 20, 0, 0)
                },
                PrimaryButtonText = _resourceLoader.GetString("Yes"),
                SecondaryButtonText = _resourceLoader.GetString("No")
            };
            var dialogResult = await _dialogService.ShowAsync(dialog);
            if (dialogResult != ContentDialogResult.Primary)
            {
                return;
            }

            ShowProgressIndicator();
            var success = await DirectoryService.Instance.DeleteResource(ResourceInfo);
            HideProgressIndicator();
            if (success)
            {
                _navigationService.GoBack();
            }
        }

        private async void RenameResource()
        {
            if (ResourceInfo == null)
            {
                return;
            }

            var dialog = new ContentDialog
            {
                Title = _resourceLoader.GetString("Rename"),
                Content = new TextBox
                {
                    Header = _resourceLoader.GetString("ChooseANewName"),
                    Text = ResourceInfo.Name,
                    Margin = new Thickness(0, 20, 0, 0)
                },
                PrimaryButtonText = _resourceLoader.GetString("Ok"),
                SecondaryButtonText = _resourceLoader.GetString("Cancel")
            };
            var dialogResult = await _dialogService.ShowAsync(dialog);
            if (dialogResult != ContentDialogResult.Primary)
            {
                return;
            }
            var textBox = dialog.Content as TextBox;
            var newName = textBox?.Text;
            if (string.IsNullOrEmpty(newName))
            {
                return;
            }
            ShowProgressIndicator();
            var success = await Directory.Rename(ResourceInfo.Name, newName);
            HideProgressIndicator();
            if (success)
            {
                _navigationService.GoBack();
            }
        }

        private void MoveResource(object obj)
        {
            if (ResourceInfo == null)
            {
                return;
            }

            var parameters = new MoveFileOrFolderPageParameters
            {
                ResourceInfo = ResourceInfo
            };
            _navigationService.Navigate(PageToken.MoveFileOrFolder.ToString(), parameters.Serialize());
        }

        private void PinToStart(object parameter)
        {
            if(ResourceInfo == null) return;
            _tileService.CreatePinnedObject(ResourceInfo);
        }

        private bool CanPinToStart(object parameter)
        {
            return ResourceInfo != null && !_tileService.IsTilePinned(ResourceInfo);
        }
    }
}