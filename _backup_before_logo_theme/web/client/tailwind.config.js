/** Royal palette for Darbar_E_Saltanat: deep navy/charcoal, gold accents, maroon highlights. */
export default {
  content: ['./index.html', './src/**/*.{js,jsx}'],
  theme: {
    extend: {
      colors: {
        royal: { 950: '#0b1020', 900: '#111831', 800: '#1a2342', 700: '#26315a' },
        gold: { 300: '#f0d68a', 400: '#e5c35c', 500: '#d4a72c', 600: '#b38a1c' },
        maroon: { 500: '#9b1c31', 600: '#7f1526', 700: '#64101e' },
      },
      fontFamily: { display: ['"Playfair Display"', 'Georgia', 'serif'], sans: ['Inter', 'system-ui', 'sans-serif'] },
    },
  },
  plugins: [],
};
