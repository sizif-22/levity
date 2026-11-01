import { afterEach, describe, it, expect, vi } from 'vitest';
import { cleanup, render, screen, fireEvent } from '@testing-library/react';
import { NotebookSheet } from '../src/components/NotebookSheet';
import type { NoteItem } from '../src/lib/notebook';

afterEach(cleanup);

describe('NotebookSheet', () => {
  const sampleNotes: NoteItem[] = [
    {
      id: 'note-1',
      title: 'First Thought',
      content: 'This is my first note with some instructions.',
      images: [
        { id: 'img-1', name: 'photo.jpg', mimeType: 'image/jpeg', url: 'data:image/jpeg;base64,123' },
      ],
      createdAt: 1000,
      updatedAt: 2000,
      pinned: true,
    },
    {
      id: 'note-2',
      title: 'Second Thought',
      content: 'Another note without images.',
      images: [],
      createdAt: 3000,
      updatedAt: 4000,
      pinned: false,
    },
  ];

  it('renders notes list with titles and previews', () => {
    const send = vi.fn();
    const onClose = vi.fn();

    render(<NotebookSheet notes={sampleNotes} send={send} onClose={onClose} />);

    expect(screen.getByText('Notebook')).toBeDefined();
    expect(screen.getByText('First Thought')).toBeDefined();
    expect(screen.getByText('Second Thought')).toBeDefined();
    expect(screen.getByText('Pinned')).toBeDefined();
  });

  it('filters notes based on search input', () => {
    const send = vi.fn();
    const onClose = vi.fn();

    render(<NotebookSheet notes={sampleNotes} send={send} onClose={onClose} />);

    const searchInput = screen.getByPlaceholderText('Search notes...');
    fireEvent.change(searchInput, { target: { value: 'instructions' } });

    expect(screen.getByText('First Thought')).toBeDefined();
    expect(screen.queryByText('Second Thought')).toBeNull();
  });

  it('allows starting a new note and sends notebook_save on save', () => {
    const send = vi.fn();
    const onClose = vi.fn();

    render(<NotebookSheet notes={sampleNotes} send={send} onClose={onClose} />);

    fireEvent.click(screen.getByText('+ New Note'));

    const titleInput = screen.getByPlaceholderText('Note title...');
    fireEvent.change(titleInput, { target: { value: 'My Brand New Note' } });

    const contentInput = screen.getByPlaceholderText(/Write your note here/);
    fireEvent.change(contentInput, { target: { value: 'Some markdown content' } });

    fireEvent.click(screen.getByText('Save note'));

    expect(send).toHaveBeenCalledWith(
      expect.objectContaining({
        type: 'notebook_save',
        note: expect.objectContaining({
          title: 'My Brand New Note',
          content: 'Some markdown content',
        }),
      })
    );
  });

  it('toggles pin status on pin button click', () => {
    const send = vi.fn();
    const onClose = vi.fn();

    render(<NotebookSheet notes={sampleNotes} send={send} onClose={onClose} />);

    const unpinBtn = screen.getByTitle('Unpin');
    fireEvent.click(unpinBtn);

    expect(send).toHaveBeenCalledWith(
      expect.objectContaining({
        type: 'notebook_save',
        note: expect.objectContaining({
          id: 'note-1',
          pinned: false,
        }),
      })
    );
  });

  it('confirms and sends notebook_delete', () => {
    const send = vi.fn();
    const onClose = vi.fn();

    const { container } = render(<NotebookSheet notes={sampleNotes} send={send} onClose={onClose} />);

    const delBtn = container.querySelector('.notebook-card-btn.del');
    expect(delBtn).toBeTruthy();
    fireEvent.click(delBtn!);

    expect(screen.getByText('Delete this note?')).toBeDefined();
    const confirmBtn = container.querySelector('.notebook-confirm-btns .btn.primary');
    expect(confirmBtn).toBeTruthy();
    fireEvent.click(confirmBtn!);

    expect(send).toHaveBeenCalledWith({
      type: 'notebook_delete',
      id: 'note-1',
    });
  });
});
