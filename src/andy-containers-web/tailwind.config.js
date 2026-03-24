/** @type {import('tailwindcss').Config} */
module.exports = {
  darkMode: 'class',
  content: ["./src/**/*.{html,ts}"],
  theme: {
    extend: {
      colors: {
        primary: {
          50: '#e6f0fa',
          100: '#cce0f5',
          200: '#99c2eb',
          300: '#66a3e0',
          400: '#3385db',
          500: '#0066cc',
          600: '#0052a3',
          700: '#003d7a',
          800: '#002952',
          900: '#001429',
          950: '#000a14',
        },
        surface: {
          50: '#f8f9fa',
          100: '#f1f3f5',
          200: '#dee2e6',
          300: '#ced4da',
          400: '#94a3b8',
          500: '#6c757d',
          600: '#495057',
          700: '#343a40',
          800: '#1e293b',
          900: '#0f172a',
          950: '#020617',
        },
        accent: {
          DEFAULT: '#00a4dc',
          secondary: '#ff6b35',
        },
      },
      fontFamily: {
        sans: ['Inter', '-apple-system', 'BlinkMacSystemFont', 'Segoe UI', 'Roboto', 'sans-serif'],
        mono: ['JetBrains Mono', 'Fira Code', 'Consolas', 'monospace'],
      },
      fontSize: {
        'xs': ['0.875rem', { lineHeight: '1.5' }],   /* 14px */
        'sm': ['1rem', { lineHeight: '1.5' }],        /* 16px */
        'base': ['1.125rem', { lineHeight: '1.6' }],  /* 18px */
        'lg': ['1.25rem', { lineHeight: '1.4' }],     /* 20px */
        'xl': ['1.5rem', { lineHeight: '1.3' }],      /* 24px */
        '2xl': ['1.75rem', { lineHeight: '1.2' }],    /* 28px */
        '3xl': ['2.25rem', { lineHeight: '1.2' }],    /* 36px */
      },
      borderRadius: {
        'sm': '4px',
        'DEFAULT': '6px',
        'md': '8px',
        'lg': '10px',
        'xl': '12px',
      },
      boxShadow: {
        'card': '0 2px 8px rgba(0, 0, 0, 0.05)',
        'card-hover': '0 4px 12px rgba(0, 0, 0, 0.1)',
        'btn-primary': '0 4px 12px rgba(0, 102, 204, 0.3)',
      },
      spacing: {
        'sidebar': '280px',
        'header': '64px',
      },
      animation: {
        'fade-in': 'fadeIn 0.3s ease-in-out',
        'slide-in': 'slideIn 0.3s ease-in-out',
      },
      keyframes: {
        fadeIn: {
          '0%': { opacity: '0' },
          '100%': { opacity: '1' },
        },
        slideIn: {
          '0%': { transform: 'translateX(-10px)', opacity: '0' },
          '100%': { transform: 'translateX(0)', opacity: '1' },
        },
      },
    },
  },
  plugins: [
    require('@tailwindcss/forms'),
    require('@tailwindcss/typography'),
  ],
}
