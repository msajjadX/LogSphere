import { useEffect, type ReactNode } from 'react';
import { X } from 'lucide-react';

interface DrawerProps {
  open: boolean;
  title: ReactNode;
  onClose: () => void;
  children: ReactNode;
  widthClass?: string;
  actions?: ReactNode;
}

export function Drawer({ open, title, onClose, children, widthClass = 'max-w-2xl', actions }: DrawerProps) {
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [open, onClose]);

  if (!open) return null;

  return (
    <div className="fixed inset-0 z-40" role="dialog" aria-modal="true">
      <div className="absolute inset-0 bg-black/40" onClick={onClose} aria-hidden="true" />
      <div
        className={`absolute inset-y-0 right-0 flex w-full ${widthClass} flex-col border-l border-gray-200 bg-white shadow-xl dark:border-gray-800 dark:bg-gray-900`}
      >
        <div className="flex items-center justify-between gap-3 border-b border-gray-200 px-4 py-3 dark:border-gray-800">
          <h2 className="min-w-0 truncate text-sm font-semibold">{title}</h2>
          <div className="flex items-center gap-2">
            {actions}
            <button type="button" onClick={onClose} aria-label="Close panel" className="btn-ghost !p-1.5">
              <X className="h-4 w-4" />
            </button>
          </div>
        </div>
        <div className="flex-1 overflow-y-auto p-4">{children}</div>
      </div>
    </div>
  );
}
