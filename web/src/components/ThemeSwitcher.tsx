import { useEffect, useState } from 'react';
import { applyTheme, readTheme, type Theme } from '../lib/theme';
import { Icon, type IconName } from './Icon';

const OPTIONS: { value: Theme; icon: IconName; label: string }[] = [
  { value: 'light', icon: 'sun', label: 'Tema claro' },
  { value: 'dark', icon: 'moon', label: 'Tema escuro' },
  { value: 'system', icon: 'system', label: 'Seguir o sistema' },
];

export function ThemeSwitcher() {
  const [theme, setTheme] = useState<Theme>(readTheme);

  useEffect(() => {
    applyTheme(theme);
  }, [theme]);

  return (
    <div className="theme-switcher" role="radiogroup" aria-label="Tema">
      {OPTIONS.map((option) => (
        <button
          key={option.value}
          type="button"
          role="radio"
          aria-checked={theme === option.value}
          aria-label={option.label}
          title={option.label}
          className={theme === option.value ? 'active' : ''}
          onClick={() => setTheme(option.value)}
        >
          <Icon name={option.icon} size={15} />
        </button>
      ))}
    </div>
  );
}
