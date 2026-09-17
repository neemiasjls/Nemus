import type { ReactNode } from 'react';
import { Icon } from './Icon';

export function PageHeader({
  title,
  subtitle,
  actions,
}: {
  title: string;
  subtitle?: ReactNode;
  actions?: ReactNode;
}) {
  return (
    <header className="page-header">
      <div>
        <h1 className="page-title">{title}</h1>
        {subtitle && <p className="page-subtitle">{subtitle}</p>}
      </div>
      {actions && <div className="page-actions">{actions}</div>}
    </header>
  );
}

export function Panel({
  title,
  actions,
  children,
  className = '',
}: {
  title?: string;
  actions?: ReactNode;
  children: ReactNode;
  className?: string;
}) {
  return (
    <section className={`panel ${className}`.trim()}>
      {(title || actions) && (
        <header className="panel-header">
          {title && <h2>{title}</h2>}
          {actions && <div className="panel-actions">{actions}</div>}
        </header>
      )}
      {children}
    </section>
  );
}

/** Estado vazio que orienta, em vez de so constatar que nao ha nada. */
export function EmptyState({
  title,
  text,
  action,
}: {
  title: string;
  text?: string;
  action?: ReactNode;
}) {
  return (
    <div className="empty-state">
      <p className="empty-title">{title}</p>
      {text && <p className="empty-text">{text}</p>}
      {action}
    </div>
  );
}

/**
 * Abre e fecha o formulario de criacao. O formulario fica fechado por padrao:
 * numa versao anterior os tres formularios viviam abertos e ocupavam tanto
 * espaco quanto os proprios dados.
 */
export function NewButton({
  open,
  label,
  onToggle,
}: {
  open: boolean;
  label: string;
  onToggle: () => void;
}) {
  return (
    <button type="button" className={open ? 'secondary' : ''} aria-expanded={open} onClick={onToggle}>
      <Icon name={open ? 'close' : 'plus'} size={15} />
      <span>{open ? 'Fechar' : label}</span>
    </button>
  );
}
