const SRC = {
  stacked: { gold: '/brand/logo-stacked-gold.svg', light: '/brand/logo-stacked-for-light.svg' },
  horizontal: { gold: '/brand/logo-horizontal-gold.svg', light: '/brand/logo-horizontal-for-light.svg' },
};
const ALT = 'Darbar-E-Saltanat — দরবার-এ-সালতানাত — Royal Mughlai Cuisine';

/**
 * Brand logo (emblem + English and Bangla name).
 *  tone="auto"    follows the light/dark theme (dark-ink version on light surfaces, gold version on dark ones)
 *  tone="onDark"  always the gold version — for surfaces that are dark in both themes (the sidebar)
 *  variant="emblem" is the round badge only (works on any background)
 */
export default function Logo({ variant = 'stacked', tone = 'auto', className = '', alt = ALT }) {
  if (variant === 'emblem') return <img src="/brand/emblem.svg" alt={alt} className={className} draggable={false} />;
  const s = SRC[variant] || SRC.stacked;
  if (tone === 'onDark') return <img src={s.gold} alt={alt} className={className} draggable={false} />;
  return (
    <>
      <img src={s.light} alt={alt} className={`${className} dark:hidden`} draggable={false} />
      <img src={s.gold} alt="" aria-hidden="true" className={`${className} hidden dark:block`} draggable={false} />
    </>
  );
}
