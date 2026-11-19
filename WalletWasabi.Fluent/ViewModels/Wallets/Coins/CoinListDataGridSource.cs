using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Controls.Templates;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Fluent.Views.CoinControl.Core.Cells;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Coins;

public static class CoinListDataGridSource
{
	public static FlatTreeDataGridSource<CoinViewModel> Create(IEnumerable<CoinViewModel> source, bool allowSelection)
	{
		// [Column]			[View]					[Header]	[Width]		[MinWidth]		[MaxWidth]	[CanUserSort]
		// Indicators		IndicatorsColumnView	-			Auto		-				-			true
		// AnonymityPoints	AnonymityPointsCellView	<custom>	50			-				-			true
		// Amount			AmountColumnView		Amount		*			-				-			true
		// Selection		SelectionColumnView		-			Auto		-				-			false
		var result = new FlatTreeDataGridSource<CoinViewModel>(source)
		{
			Columns =
			{
				IndicatorsColumn(),
				AnonymityPointsColumn(),
				AmountColumn(),
			}
		};

		if (allowSelection)
		{
			result.Columns.Add(SelectionColumn());
		}

		return result;
	}

	private static int GetIndicatorPriority(CoinViewModel x)
	{
		if (x.IsCoinjoining)
		{
			return 1;
		}

		if (x.BannedUntilUtc.HasValue)
		{
			return 2;
		}

		if (!x.IsConfirmed)
		{
			return 3;
		}

		if (x.IsSelectedForCoinJoin)
		{
			return 4;
		}

		return 0;
	}

	private static IColumn<CoinViewModel> IndicatorsColumn()
	{
		return new TemplateColumn<CoinViewModel>(
			null,
			new FuncDataTemplate<CoinViewModel>((_, _) => new IndicatorsCellView(), true),
			null,
			GridLength.Auto,
			new TemplateColumnOptions<CoinViewModel>
			{
				CompareAscending = Sort<CoinViewModel>.Ascending(GetIndicatorPriority),
				CompareDescending = Sort<CoinViewModel>.Descending(GetIndicatorPriority)
			});
	}

	private static TemplateColumn<CoinViewModel> SelectionColumn()
	{
		return new TemplateColumn<CoinViewModel>(
			null,
			new FuncDataTemplate<CoinViewModel>(
				(_, _) => new SelectionCellView(),
				true),
			null,
			GridLength.Auto);
	}

	private static IColumn<CoinViewModel> AmountColumn()
	{
		return new TemplateColumn<CoinViewModel>(
			null,
			new FuncDataTemplate<CoinViewModel>((_, _) => new AmountCellView(), true),
			null,
			GridLength.Star,
			new TemplateColumnOptions<CoinViewModel>
			{
				CompareAscending = Sort<CoinViewModel>.Ascending(x => x.Amount.Btc),
				CompareDescending = Sort<CoinViewModel>.Descending(x => x.Amount.Btc)
			});
	}

	private static IColumn<CoinViewModel> AnonymityPointsColumn()
	{
		// A coin whose points are not known yet counts as one with less than none.
		return new TemplateColumn<CoinViewModel>(
			null,
			new FuncDataTemplate<CoinViewModel>((_, _) => new AnonymityPointsCellView(), true),
			null,
			GridLength.Auto,
			new TemplateColumnOptions<CoinViewModel>
			{
				CompareAscending = Sort<CoinViewModel>.Ascending(x => x.AnonymityPoints ?? -1),
				CompareDescending = Sort<CoinViewModel>.Descending(x => x.AnonymityPoints ?? -1)
			});
	}
}
