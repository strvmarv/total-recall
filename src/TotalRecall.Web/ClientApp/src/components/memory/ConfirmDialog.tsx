import type { ReactNode } from 'react';

export function ConfirmDialog({ title, body, confirmLabel, onConfirm, onCancel, danger }: {
  title: string;
  body?: ReactNode;
  confirmLabel: string;
  onConfirm: () => void;
  onCancel: () => void;
  danger?: boolean;
}) {
  return (
    <div className="tr-modal-backdrop" role="presentation" onClick={onCancel}>
      <div className="tr-modal" role="dialog" aria-modal="true" aria-label={title} onClick={(e) => e.stopPropagation()}>
        <h3>{title}</h3>
        {body && <div className="tr-modal-body">{body}</div>}
        <div className="tr-modal-actions">
          <button type="button" className="tr-btn" onClick={onCancel}>Cancel</button>
          <button type="button" className={danger ? 'tr-btn tr-btn-danger' : 'tr-btn tr-btn-primary'} onClick={onConfirm}>{confirmLabel}</button>
        </div>
      </div>
    </div>
  );
}
