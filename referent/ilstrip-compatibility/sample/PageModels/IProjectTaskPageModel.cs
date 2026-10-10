using CommunityToolkit.Mvvm.Input;
using MauiStripSample.Models;

namespace MauiStripSample.PageModels;

public interface IProjectTaskPageModel
{
	IAsyncRelayCommand<ProjectTask> NavigateToTaskCommand { get; }
	bool IsBusy { get; }
}