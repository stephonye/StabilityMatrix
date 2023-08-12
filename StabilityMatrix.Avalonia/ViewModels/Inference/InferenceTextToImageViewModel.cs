﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AsyncAwaitBestPractices;
using Avalonia.Media.Imaging;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NLog;
using Refit;
using SkiaSharp;
using StabilityMatrix.Avalonia.Controls;
using StabilityMatrix.Avalonia.Extensions;
using StabilityMatrix.Avalonia.Helpers;
using StabilityMatrix.Avalonia.Models;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.Views;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Inference;
using StabilityMatrix.Core.Models.Api.Comfy;
using StabilityMatrix.Core.Models.Api.Comfy.WebSocketData;
#pragma warning disable CS0657 // Not a valid attribute location for this declaration

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(InferenceTextToImageView))]
public partial class InferenceTextToImageViewModel : InferenceTabViewModelBase
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly INotificationService notificationService;
    private readonly ServiceManager<ViewModelBase> vmFactory;

    public IInferenceClientManager ClientManager { get; }
    
    public ImageGalleryCardViewModel ImageGalleryCardViewModel { get; }
    public PromptCardViewModel PromptCardViewModel { get; }
    public StackCardViewModel StackCardViewModel { get; }

    public UpscalerCardViewModel UpscalerCardViewModel => 
        StackCardViewModel
        .GetCard<StackExpanderViewModel>()
        .GetCard<UpscalerCardViewModel>();

    public SamplerCardViewModel HiresSamplerCardViewModel =>
        StackCardViewModel
            .GetCard<StackExpanderViewModel>()
            .GetCard<SamplerCardViewModel>();

    public bool IsHiresFixEnabled => StackCardViewModel.GetCard<StackExpanderViewModel>().IsEnabled;
    
    [JsonIgnore]
    public ProgressViewModel OutputProgress { get; } = new();

    [ObservableProperty]
    [property: JsonIgnore]
    private string? outputImageSource;

    public InferenceTextToImageViewModel(
        INotificationService notificationService,
        IInferenceClientManager inferenceClientManager,
        ServiceManager<ViewModelBase> vmFactory
    )
    {
        this.notificationService = notificationService;
        this.vmFactory = vmFactory;
        ClientManager = inferenceClientManager;

        // Get sub view models from service manager
        
        var seedCard = vmFactory.Get<SeedCardViewModel>();        
        seedCard.GenerateNewSeed();

        ImageGalleryCardViewModel = vmFactory.Get<ImageGalleryCardViewModel>();
        PromptCardViewModel = vmFactory.Get<PromptCardViewModel>();

        StackCardViewModel = vmFactory.Get<StackCardViewModel>();
            
        StackCardViewModel.AddCards(new LoadableViewModelBase[]
        {
            // Model Card
            vmFactory.Get<ModelCardViewModel>(),
            // Sampler
            vmFactory.Get<SamplerCardViewModel>(),
            // Hires Fix
            vmFactory.Get<StackExpanderViewModel>(stackExpander =>
            {
                stackExpander.Title = "Hires Fix";
                stackExpander.AddCards(new LoadableViewModelBase[]
                {
                    // Hires Fix Upscaler
                    vmFactory.Get<UpscalerCardViewModel>(),
                    // Hires Fix Sampler
                    vmFactory.Get<SamplerCardViewModel>(samplerCard =>
                    {
                        samplerCard.IsDimensionsEnabled = false;
                        samplerCard.IsCfgScaleEnabled = false;
                        samplerCard.IsSamplerSelectionEnabled = false;
                        samplerCard.IsDenoiseStrengthEnabled = true;
                    })
                });
            }),
            // Seed
            seedCard,
            // Batch Size
            vmFactory.Get<BatchSizeCardViewModel>(),
        });

        GenerateImageCommand.WithNotificationErrorHandler(notificationService);
    }

    private Dictionary<string, ComfyNode> GetCurrentPrompt()
    {
        var sampler = StackCardViewModel.GetCard<SamplerCardViewModel>();
        var batchCard = StackCardViewModel.GetCard<BatchSizeCardViewModel>();
        var modelCard = StackCardViewModel.GetCard<ModelCardViewModel>();
        var seedCard = StackCardViewModel.GetCard<SeedCardViewModel>();
        
        var prompt = new Dictionary<string, ComfyNode>
        {
            ["CheckpointLoader"] = new()
            {
                ClassType = "CheckpointLoaderSimple",
                Inputs = new Dictionary<string, object?>
                {
                    ["ckpt_name"] = modelCard.SelectedModelName
                }
            },
            ["EmptyLatentImage"] = new()
            {
                ClassType = "EmptyLatentImage",
                Inputs = new Dictionary<string, object?>
                {
                    ["batch_size"] = batchCard.BatchSize,
                    ["height"] = sampler.Height,
                    ["width"] = sampler.Width,
                }
            },
            ["Sampler"] = new()
            {
                ClassType = "KSampler",
                Inputs = new Dictionary<string, object?>
                {
                    ["cfg"] = sampler.CfgScale,
                    ["denoise"] = 1,
                    ["latent_image"] = new object[] { "EmptyLatentImage", 0 },
                    ["model"] = new object[] { "CheckpointLoader", 0 },
                    ["negative"] = new object[] { "NegativeCLIP", 0 },
                    ["positive"] = new object[] { "PositiveCLIP", 0 },
                    ["sampler_name"] = sampler.SelectedSampler?.Name,
                    ["scheduler"] = "normal",
                    ["seed"] = seedCard.Seed,
                    ["steps"] = sampler.Steps
                }
            },
            ["PositiveCLIP"] = new()
            {
                ClassType = "CLIPTextEncode",
                Inputs = new Dictionary<string, object?>
                {
                    ["clip"] = new object[] { "CheckpointLoader", 1 },
                    ["text"] = PromptCardViewModel.PromptDocument.Text,
                }
            },
            ["NegativeCLIP"] = new()
            {
                ClassType = "CLIPTextEncode",
                Inputs = new Dictionary<string, object?>
                {
                    ["clip"] = new object[] { "CheckpointLoader", 1 },
                    ["text"] = PromptCardViewModel.NegativePromptDocument.Text,
                }
            },
            ["VAEDecoder"] = new()
            {
                ClassType = "VAEDecode",
                Inputs = new Dictionary<string, object?>
                {
                    ["samples"] = new object[] { "Sampler", 0 },
                    ["vae"] = new object[] { "CheckpointLoader", 2 }
                }
            },
            ["SaveImage"] = new()
            {
                ClassType = "SaveImage",
                Inputs = new Dictionary<string, object?>
                {
                    ["filename_prefix"] = "SM-Inference",
                    ["images"] = new object[] { "VAEDecoder", 0 }
                }
            }
        };
        
        // If hi-res fix is enabled, add the LatentUpscale node and another KSampler node
        if (IsHiresFixEnabled)
        {
            var hiresUpscalerCard = UpscalerCardViewModel;
            var hiresSamplerCard = HiresSamplerCardViewModel;
            
            prompt["LatentUpscale"] = new ComfyNode
            {
                ClassType = "LatentUpscale",
                Inputs = new Dictionary<string, object?>
                {
                    ["upscale_method"] = hiresUpscalerCard.SelectedUpscaler?.Name,
                    ["width"] = sampler.Width * hiresUpscalerCard.Scale,
                    ["height"] = sampler.Height * hiresUpscalerCard.Scale,
                    ["crop"] = "disabled",
                    ["samples"] = new object[] { "Sampler", 0 }
                }
            };

            prompt["Sampler2"] = new ComfyNode
            {
                ClassType = "KSampler",
                Inputs = new Dictionary<string, object?>
                {
                    ["cfg"] = hiresSamplerCard.CfgScale,
                    ["denoise"] = hiresSamplerCard.DenoiseStrength,
                    ["latent_image"] = new object[] { "LatentUpscale", 0 },
                    ["model"] = new object[] { "CheckpointLoader", 0 },
                    ["negative"] = new object[] { "NegativeCLIP", 0 },
                    ["positive"] = new object[] { "PositiveCLIP", 0 },
                    // Use hires sampler name if not null, otherwise use the normal sampler name
                    ["sampler_name"] = hiresSamplerCard.SelectedSampler?.Name ?? sampler.SelectedSampler?.Name,
                    ["scheduler"] = "normal",
                    ["seed"] = seedCard.Seed,
                    ["steps"] = hiresSamplerCard.Steps
                }
            };
            
            // Reroute the VAEDecoder's input to be from Sampler2
            prompt["VAEDecoder"].Inputs["samples"] = new object[] { "Sampler2", 0 };
        }
        
        return prompt;
    }

    private void OnProgressUpdateReceived(object? sender, ComfyProgressUpdateEventArgs args)
    {
        OutputProgress.Value = args.Value;
        OutputProgress.Maximum = args.Maximum;
        OutputProgress.IsIndeterminate = false;

        OutputProgress.Text = $"({args.Value} / {args.Maximum})" 
                              + (args.RunningNode != null ? $" {args.RunningNode}" : "");
    }

    private void OnPreviewImageReceived(object? sender, ComfyWebSocketImageData args)
    {
        // Decode to bitmap
        using var stream = new MemoryStream(args.ImageBytes);
        var bitmap = new Bitmap(stream);

        ImageGalleryCardViewModel.PreviewImage?.Dispose();
        ImageGalleryCardViewModel.PreviewImage = bitmap;
        ImageGalleryCardViewModel.IsPreviewOverlayEnabled = true;
    }

    private async Task GenerateImageImpl(CancellationToken cancellationToken = default)
    {
        if (!ClientManager.IsConnected)
        {
            notificationService.Show("Client not connected", "Please connect first");
            return;
        }

        // If enabled, randomize the seed
        var seedCard = StackCardViewModel.GetCard<SeedCardViewModel>();
        if (seedCard.IsRandomizeEnabled)
        {
            seedCard.GenerateNewSeed();
        }

        var client = ClientManager.Client;

        var nodes = GetCurrentPrompt();

        // Connect progress handler
        // client.ProgressUpdateReceived += OnProgressUpdateReceived;
        client.PreviewImageReceived += OnPreviewImageReceived;

        ComfyTask? promptTask = null;
        try
        {
            // Register to interrupt if user cancels
            cancellationToken.Register(() =>
            {
                Logger.Info("Cancelling prompt");
                client.InterruptPromptAsync(new CancellationTokenSource(5000).Token).SafeFireAndForget();
            });
            
            try
            {
                promptTask = await client.QueuePromptAsync(nodes, cancellationToken);
            }
            catch (ApiException e)
            {
                Logger.Warn(e, "Api exception while queuing prompt");
                await DialogHelper.CreateApiExceptionDialog(e, "Api Error").ShowAsync();
                return;
            }
            
            // Register progress handler
            promptTask.ProgressUpdate += OnProgressUpdateReceived;

            // Wait for prompt to finish
            await promptTask.Task.WaitAsync(cancellationToken);
            Logger.Trace($"Prompt task {promptTask.Id} finished");

            // Get output images
            var outputs = await client.GetImagesForExecutedPromptAsync(
                promptTask.Id,
                cancellationToken
            );

            ImageGalleryCardViewModel.ImageSources.Clear();
            
            var images = outputs["SaveImage"];
            if (images is null) return;

            List<ImageSource> outputImages;
            // Use local file path if available, otherwise use remote URL
            if (client.OutputImagesDir is { } outputPath)
            {
                outputImages = images
                    .Select(i => new ImageSource(i.ToFilePath(outputPath)))
                    .ToList();
            }
            else
            {
                outputImages = images
                    .Select(i => new ImageSource(i.ToUri(client.BaseAddress)))
                    .ToList();
            }
            
            // Download all images to make grid, if multiple
            if (outputImages.Count > 1)
            {
                var loadedImages = outputImages.Select(i =>
                    SKImage.FromEncodedData(i.LocalFile?.Info.OpenRead())).ToImmutableArray();

                var grid = ImageProcessor.CreateImageGrid(loadedImages);
                
                // Save to disk
                var lastName = outputImages.Last().LocalFile?.Info.Name;
                var gridPath = client.OutputImagesDir!.JoinFile($"grid-{lastName}");

                await using var fileStream = gridPath.Info.OpenWrite();
                await fileStream.WriteAsync(grid.Encode().ToArray(), cancellationToken);
                
                // Insert to start of gallery
                ImageGalleryCardViewModel.ImageSources.Add(new ImageSource(gridPath));
                // var bitmaps = (await outputImages.SelectAsync(async i => await i.GetBitmapAsync())).ToImmutableArray();
            }
            
            // Insert rest of images
            ImageGalleryCardViewModel.ImageSources.AddRange(outputImages);
        }
        finally
        {
            // Disconnect progress handler
            OutputProgress.Value = 0;
            OutputProgress.Text = "";
            ImageGalleryCardViewModel.PreviewImage?.Dispose();
            ImageGalleryCardViewModel.PreviewImage = null;
            ImageGalleryCardViewModel.IsPreviewOverlayEnabled = false;
            
            // client.ProgressUpdateReceived -= OnProgressUpdateReceived;
            promptTask?.Dispose();
            client.PreviewImageReceived -= OnPreviewImageReceived;
        }
    }

    [RelayCommand(IncludeCancelCommand = true, FlowExceptionsToTaskScheduler = true)]
    private async Task GenerateImage(CancellationToken cancellationToken = default)
    {
        try
        {
            await GenerateImageImpl(cancellationToken);
        }
        catch (OperationCanceledException e)
        {
            Logger.Debug($"[Image Generation Canceled] {e.Message}");
        }
    }
}
