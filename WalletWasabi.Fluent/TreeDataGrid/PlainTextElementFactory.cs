using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace WalletWasabi.Fluent.TreeDataGrid;

/// <summary>Makes the table draw the cells of a <see cref="PlainTextColumn{T}"/> with a <see cref="TreeDataGridPlainTextCell"/>.</summary>
internal class PlainTextElementFactory : TreeDataGridElementFactory
{
	protected override Control CreateElement(object? data)
	{
		if (data is PlainTextCell)
		{
			return new TreeDataGridPlainTextCell();
		}

		return base.CreateElement(data);
	}

	protected override string GetDataRecycleKey(object? data)
	{
		if (data is PlainTextCell)
		{
			return typeof(TreeDataGridPlainTextCell).FullName!;
		}

		return base.GetDataRecycleKey(data);
	}
}
