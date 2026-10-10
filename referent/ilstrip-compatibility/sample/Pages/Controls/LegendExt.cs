using Syncfusion.Maui.Toolkit.Charts;

namespace MauiStripSample.Pages.Controls;

public class LegendExt : ChartLegend
{
	protected override double GetMaximumSizeCoefficient()
	{
		return 0.5;
	}
}
