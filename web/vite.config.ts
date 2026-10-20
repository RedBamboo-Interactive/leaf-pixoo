import {defineConfig} from 'vite'
import react from '@vitejs/plugin-react'
import {fileURLToPath} from 'node:url'
import {federation} from '@module-federation/vite'
import tailwindcss from '@tailwindcss/vite'

export default defineConfig(({command})=>({
  resolve:{dedupe:['react','react-dom']},
  plugins:[react(),tailwindcss(), ...(command==='build' ? [federation({
    name:'pixoo',filename:'remoteEntry.js',exposes:{'.':'./src/index.tsx'},
    shared:{react:{singleton:true,requiredVersion:false},'react/jsx-runtime':{singleton:true,requiredVersion:false},
      'react-dom':{singleton:true,requiredVersion:false},'react-dom/client':{singleton:true,requiredVersion:false},
      '@redbamboo/ui':{singleton:true,requiredVersion:false},
      '@redbamboo/utility':{singleton:true,requiredVersion:false}},dts:false,
  })] : [])],
  define:{'process.env.NODE_ENV':JSON.stringify(command==='build'?'production':'development')},
  build:{outDir:process.env.PIXOO_WEB_OUTPUT ?? 'dist',emptyOutDir:true,target:'es2023',
    lib:{entry:fileURLToPath(new URL('./src/index.tsx',import.meta.url)),formats:['es'],fileName:()=> 'plugin.js',cssFileName:'plugin'},
    rollupOptions:{output:{chunkFileNames:'chunks/[name]-[hash].js'}}},
}))
