using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Controls.Templates;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Fluent.Views.Wallets.Transactions.Inputs.Cells;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Transactions.Inputs;

public static class InputsCoinListDataGridSource
{
	public static HierarchicalTreeDataGridSource<InputsCoinListItem> Create(IEnumerable<InputsCoinListItem> source)
	{
		// [Column]			[View]					[Header]	[Width]		[MinWidth]		[MaxWidth]	[CanUserSort]
		// Expander			-						-			3 Star		-				-			false
		// Amount			AmountColumnView		Amount		10 Star		-				-			true
		var result = new HierarchicalTreeDataGridSource<InputsCoinListItem>(source)
		{
			Columns =
			{
				ExpanderColumn(),
				AmountColumn(),
			}
		};
		return result;
	}

	private static IColumn<InputsCoinListItem> AmountColumn()
	{
		return new TemplateColumn<InputsCoinListItem>(
			null,
			new FuncDataTemplate<InputsCoinListItem>((item, _) => item is { IsChild: true } ? new AmountCellView() : new TextCellView()),
			null,
			new GridLength(10, GridUnitType.Star),
			new TemplateColumnOptions<InputsCoinListItem>
			{
				CompareAscending = Sort<InputsCoinListItem>.Ascending(x => x.Amount.Btc),
				CompareDescending = Sort<InputsCoinListItem>.Descending(x => x.Amount.Btc)
			});
	}

	/// <summary>The column that opens and closes the list of the inputs. It shows nothing of an input.</summary>
	private static IColumn<InputsCoinListItem> ExpanderColumn()
	{
		return new HierarchicalExpanderColumn<InputsCoinListItem>(
			new TemplateColumn<InputsCoinListItem>(
				null,
				new FuncDataTemplate<InputsCoinListItem>((_, _) => new Panel(), true),
				null,
				new GridLength(3, GridUnitType.Star)),
			group => group.Children,
			node => true,
			node => node.IsExpanded);
	}
}
