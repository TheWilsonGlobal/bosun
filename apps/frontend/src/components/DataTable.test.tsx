import { describe, expect, it, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { DataTable, type Column } from './DataTable';

interface TestItem {
  id: number;
  name: string;
  category: string;
}

const testData: TestItem[] = Array.from({ length: 30 }, (_, i) => ({
  id: i + 1,
  name: `Item ${i + 1}`,
  category: i % 2 === 0 ? 'Mechanical' : 'Electrical',
}));

const columns: Column<TestItem>[] = [
  { key: 'id', header: 'ID', width: 50 },
  { key: 'name', header: 'Name', render: (item) => <span>{item.name}</span> },
  { key: 'category', header: 'Category' },
];

describe('DataTable abstraction component', () => {
  it('renders columns and data with zebra striped rows', () => {
    const { container } = render(
      <DataTable
        data={testData.slice(0, 5)}
        columns={columns}
        caption="Test items"
        striped
      />,
    );

    expect(screen.getByText('Item 1')).toBeInTheDocument();
    expect(screen.getByText('Item 5')).toBeInTheDocument();

    // Check zebra striping classes on rows
    const rows = container.querySelectorAll('tbody tr');
    expect(rows[0]).toHaveClass('bg-card');
    expect(rows[1]).toHaveClass('bg-muted/20');
    expect(rows[2]).toHaveClass('bg-card');
  });

  it('paginates data in clientPagination mode', () => {
    render(
      <DataTable
        data={testData}
        columns={columns}
        clientPagination
        initialPageSize={10}
      />,
    );

    // Initial page shows items 1-10
    expect(screen.getByText(/Showing/i)).toHaveTextContent('Showing 1–10 of 30 records');
    expect(screen.getByText('Item 1')).toBeInTheDocument();
    expect(screen.getByText('Item 10')).toBeInTheDocument();
    expect(screen.queryByText('Item 11')).toBeNull();

    // Go to next page
    const nextBtn = screen.getByRole('button', { name: 'Next page' });
    fireEvent.click(nextBtn);

    expect(screen.getByText(/Showing/i)).toHaveTextContent('Showing 11–20 of 30 records');
    expect(screen.getByText('Item 11')).toBeInTheDocument();
    expect(screen.queryByText('Item 1')).toBeNull();

    // Go to last page
    const lastBtn = screen.getByRole('button', { name: 'Last page' });
    fireEvent.click(lastBtn);

    expect(screen.getByText(/Showing/i)).toHaveTextContent('Showing 21–30 of 30 records');
    expect(screen.getByText('Item 30')).toBeInTheDocument();
  });

  it('triggers onRowClick when clicking or pressing Enter on a row', () => {
    const onRowClick = vi.fn();
    render(
      <DataTable
        data={testData.slice(0, 3)}
        columns={columns}
        onRowClick={onRowClick}
      />,
    );

    const firstRow = screen.getByText('Item 1').closest('tr')!;
    fireEvent.click(firstRow);
    expect(onRowClick).toHaveBeenCalledWith(testData[0], 0);

    fireEvent.keyDown(firstRow, { key: 'Enter' });
    expect(onRowClick).toHaveBeenCalledTimes(2);
  });
});
