import { useState, useRef, useMemo } from 'react';
import type { NoteItem, NoteImage } from '../lib/notebook';
import { processImageFile } from '../lib/images';
import { Markdown } from './Markdown';

interface Props {
  notes: NoteItem[];
  send: (msg: Record<string, unknown>) => void;
  onClose: () => void;
}

export function NotebookSheet({ notes, send, onClose }: Props) {
  const [editingNote, setEditingNote] = useState<NoteItem | null>(null);
  const [search, setSearch] = useState('');
  const [previewMarkdown, setPreviewMarkdown] = useState(false);
  const [confirmDeleteId, setConfirmDeleteId] = useState<string | null>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);

  // Form state for current editor
  const [title, setTitle] = useState('');
  const [content, setContent] = useState('');
  const [images, setImages] = useState<NoteImage[]>([]);
  const [pinned, setPinned] = useState(false);

  const startNewNote = () => {
    const fresh: NoteItem = {
      id: '',
      title: '',
      content: '',
      images: [],
      createdAt: Date.now(),
      updatedAt: Date.now(),
      pinned: false,
    };
    setTitle('');
    setContent('');
    setImages([]);
    setPinned(false);
    setPreviewMarkdown(false);
    setEditingNote(fresh);
  };

  const startEditNote = (n: NoteItem) => {
    setTitle(n.title);
    setContent(n.content);
    setImages(n.images ? [...n.images] : []);
    setPinned(n.pinned ?? false);
    setPreviewMarkdown(false);
    setEditingNote(n);
  };

  const handleSave = () => {
    if (!editingNote) return;
    const toSave: NoteItem = {
      id: editingNote.id,
      title: title.trim() || 'Untitled Note',
      content,
      images,
      createdAt: editingNote.createdAt || Date.now(),
      updatedAt: Date.now(),
      pinned,
    };
    send({ type: 'notebook_save', note: toSave });
    setEditingNote(null);
  };

  const handleDelete = (id: string) => {
    send({ type: 'notebook_delete', id });
    setConfirmDeleteId(null);
    if (editingNote?.id === id) {
      setEditingNote(null);
    }
  };

  const handleTogglePin = (n: NoteItem, e: React.MouseEvent) => {
    e.stopPropagation();
    const updated: NoteItem = {
      ...n,
      pinned: !n.pinned,
      updatedAt: Date.now(),
    };
    send({ type: 'notebook_save', note: updated });
  };

  const handleAddImages = async (files: FileList | null) => {
    if (!files || files.length === 0) return;
    for (let i = 0; i < files.length; i++) {
      const file = files[i];
      if (!file.type.startsWith('image/')) continue;
      try {
        const processed = await processImageFile(file);
        const newImg: NoteImage = {
          id: 'img_' + Math.random().toString(36).slice(2, 10),
          name: processed.name,
          mimeType: processed.type,
          url: `data:${processed.type};base64,${processed.data}`,
          data: processed.data,
        };
        setImages((prev) => [...prev, newImg]);
      } catch (err) {
        console.error('Failed to process image', err);
      }
    }
    if (fileInputRef.current) fileInputRef.current.value = '';
  };

  const handlePaste = (e: React.ClipboardEvent) => {
    const items = e.clipboardData?.items;
    if (!items) return;
    const imageFiles: File[] = [];
    for (let i = 0; i < items.length; i++) {
      if (items[i].type.indexOf('image') !== -1) {
        const file = items[i].getAsFile();
        if (file) imageFiles.push(file);
      }
    }
    if (imageFiles.length > 0) {
      e.preventDefault();
      const dt = new DataTransfer();
      imageFiles.forEach((f) => dt.items.add(f));
      void handleAddImages(dt.files);
    }
  };

  const filteredNotes = useMemo(() => {
    if (!search.trim()) return notes;
    const q = search.toLowerCase();
    return notes.filter(
      (n) => n.title.toLowerCase().includes(q) || n.content.toLowerCase().includes(q)
    );
  }, [notes, search]);

  const formatDate = (ts: number) => {
    if (!ts) return '';
    const d = new Date(ts);
    return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
  };

  return (
    <section className="sheet open notebook-sheet" aria-hidden="false">
      <div className="sheet-head">
        <div>
          <h3>Notebook</h3>
          <p className="sheet-sub">
            {editingNote
              ? editingNote.id ? 'Edit note' : 'New note'
              : `${notes.length} personal note${notes.length === 1 ? '' : 's'}`}
          </p>
        </div>
        <button className="icon-btn" onClick={onClose} title="Close notebook">
          <svg viewBox="0 0 24 24"><line x1="18" y1="6" x2="6" y2="18" /><line x1="6" y1="6" x2="18" y2="18" /></svg>
        </button>
      </div>

      <div className="sheet-body notebook-body">
        {editingNote ? (
          <div className="notebook-editor" onPaste={handlePaste}>
            <div className="notebook-editor-top">
              <input
                type="text"
                className="notebook-title-input"
                placeholder="Note title..."
                value={title}
                autoFocus
                onChange={(e) => setTitle(e.target.value)}
              />
              <button
                type="button"
                className={`notebook-pin-btn ${pinned ? 'pinned' : ''}`}
                onClick={() => setPinned(!pinned)}
                title={pinned ? 'Unpin note' : 'Pin note to top'}
              >
                <svg viewBox="0 0 24 24" width="16" height="16" fill={pinned ? 'currentColor' : 'none'} stroke="currentColor" strokeWidth="2">
                  <path d="M16 2v5l2 3v2H6v-2l2-3V2z" />
                  <line x1="12" y1="12" x2="12" y2="21" />
                </svg>
              </button>
            </div>

            <div className="notebook-toolbar">
              <button
                type="button"
                className={`btn sm ${!previewMarkdown ? 'primary' : 'ghost'}`}
                onClick={() => setPreviewMarkdown(false)}
              >
                Write
              </button>
              <button
                type="button"
                className={`btn sm ${previewMarkdown ? 'primary' : 'ghost'}`}
                onClick={() => setPreviewMarkdown(true)}
              >
                Preview
              </button>
              <div className="notebook-toolbar-spacer" />
              <button
                type="button"
                className="btn sm ghost"
                onClick={() => fileInputRef.current?.click()}
                title="Attach an image from file"
              >
                <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" strokeWidth="2">
                  <rect x="3" y="3" width="18" height="18" rx="2" ry="2" />
                  <circle cx="8.5" cy="8.5" r="1.5" />
                  <polyline points="21 15 16 10 5 21" />
                </svg>
                <span>Add image</span>
              </button>
              <input
                ref={fileInputRef}
                type="file"
                accept="image/*"
                multiple
                style={{ display: 'none' }}
                onChange={(e) => void handleAddImages(e.target.files)}
              />
            </div>

            {images.length > 0 && (
              <div className="notebook-editor-images">
                {images.map((img, idx) => (
                  <div key={img.id || idx} className="notebook-img-badge">
                    <img src={img.url || (img.data ? `data:${img.mimeType};base64,${img.data}` : '')} alt={img.name} />
                    <button
                      type="button"
                      className="notebook-img-del"
                      title="Remove image"
                      onClick={() => setImages((prev) => prev.filter((_, i) => i !== idx))}
                    >
                      &times;
                    </button>
                  </div>
                ))}
              </div>
            )}

            {previewMarkdown ? (
              <div className="notebook-preview">
                {content.trim() ? (
                  <Markdown text={content} />
                ) : (
                  <p className="notebook-empty-preview">Nothing to preview yet.</p>
                )}
              </div>
            ) : (
              <textarea
                className="notebook-textarea"
                placeholder="Write your note here... (Markdown, code, and pasted images supported)"
                value={content}
                rows={12}
                onChange={(e) => setContent(e.target.value)}
              />
            )}

            <div className="notebook-editor-actions">
              <button className="btn primary" onClick={handleSave}>
                Save note
              </button>
              <button className="btn ghost" onClick={() => setEditingNote(null)}>
                Cancel
              </button>
            </div>
          </div>
        ) : (
          <div className="notebook-list-view">
            <div className="notebook-top-bar">
              <div className="notebook-search-wrap">
                <svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" strokeWidth="2">
                  <circle cx="11" cy="11" r="8" />
                  <line x1="21" y1="21" x2="16.65" y2="16.65" />
                </svg>
                <input
                  type="text"
                  placeholder="Search notes..."
                  value={search}
                  onChange={(e) => setSearch(e.target.value)}
                  className="notebook-search"
                />
                {search && (
                  <button className="notebook-clear-search" onClick={() => setSearch('')}>
                    &times;
                  </button>
                )}
              </div>
              <button className="btn primary notebook-new-btn" onClick={startNewNote}>
                + New Note
              </button>
            </div>

            {filteredNotes.length === 0 ? (
              <div className="notebook-empty">
                <svg viewBox="0 0 24 24" width="36" height="36" fill="none" stroke="currentColor" strokeWidth="1.5">
                  <path d="M4 19.5A2.5 2.5 0 0 1 6.5 17H20" />
                  <path d="M6.5 2H20v20H6.5A2.5 2.5 0 0 1 4 19.5v-15A2.5 2.5 0 0 1 6.5 2z" />
                </svg>
                <h4>{search ? 'No matching notes' : 'Your notebook is empty'}</h4>
                <p>
                  {search
                    ? 'Try different search keywords.'
                    : 'Notes you write here are private and not tied to any project directory.'}
                </p>
                {!search && (
                  <button className="btn primary" onClick={startNewNote} style={{ marginTop: '8px' }}>
                    Create first note
                  </button>
                )}
              </div>
            ) : (
              <div className="notebook-cards">
                {filteredNotes.map((n) => (
                  <div
                    key={n.id}
                    className={`notebook-card ${n.pinned ? 'is-pinned' : ''}`}
                    onClick={() => startEditNote(n)}
                  >
                    <div className="notebook-card-header">
                      <h4 className="notebook-card-title">{n.title || 'Untitled Note'}</h4>
                      <div className="notebook-card-actions">
                        <button
                          className={`notebook-card-btn ${n.pinned ? 'active' : ''}`}
                          onClick={(e) => handleTogglePin(n, e)}
                          title={n.pinned ? 'Unpin' : 'Pin to top'}
                        >
                          <svg viewBox="0 0 24 24" width="14" height="14" fill={n.pinned ? 'currentColor' : 'none'} stroke="currentColor" strokeWidth="2">
                            <path d="M16 2v5l2 3v2H6v-2l2-3V2z" />
                            <line x1="12" y1="12" x2="12" y2="21" />
                          </svg>
                        </button>
                        <button
                          className="notebook-card-btn del"
                          onClick={(e) => {
                            e.stopPropagation();
                            setConfirmDeleteId(n.id);
                          }}
                          title="Delete note"
                        >
                          <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" strokeWidth="2">
                            <line x1="18" y1="6" x2="6" y2="18" />
                            <line x1="6" y1="6" x2="18" y2="18" />
                          </svg>
                        </button>
                      </div>
                    </div>

                    <p className="notebook-card-snippet">
                      {n.content ? n.content.slice(0, 150) + (n.content.length > 150 ? '...' : '') : '(No text)'}
                    </p>

                    {n.images && n.images.length > 0 && (
                      <div className="notebook-card-images">
                        {n.images.slice(0, 3).map((img, i) => (
                          <div key={img.id || i} className="notebook-card-thumb">
                            <img src={img.url || (img.data ? `data:${img.mimeType};base64,${img.data}` : '')} alt={img.name} />
                          </div>
                        ))}
                        {n.images.length > 3 && (
                          <span className="notebook-card-more">+{n.images.length - 3}</span>
                        )}
                      </div>
                    )}

                    <div className="notebook-card-foot">
                      <span className="notebook-card-date">{formatDate(n.updatedAt)}</span>
                      {n.pinned && <span className="notebook-card-tag">Pinned</span>}
                    </div>

                    {confirmDeleteId === n.id && (
                      <div className="notebook-confirm-del" onClick={(e) => e.stopPropagation()}>
                        <span>Delete this note?</span>
                        <div className="notebook-confirm-btns">
                          <button className="btn sm primary" onClick={() => handleDelete(n.id)}>
                            Delete
                          </button>
                          <button className="btn sm ghost" onClick={() => setConfirmDeleteId(null)}>
                            Cancel
                          </button>
                        </div>
                      </div>
                    )}
                  </div>
                ))}
              </div>
            )}
          </div>
        )}
      </div>
    </section>
  );
}
