using MauiStripSample.Models;
using MauiStripSample.PageModels;

namespace MauiStripSample.Pages;

public partial class MainPage : ContentPage
{
	public MainPage(MainPageModel model)
	{
		InitializeComponent();
		BindingContext = model;
	}
}