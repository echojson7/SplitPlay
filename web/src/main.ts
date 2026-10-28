// SPDX-License-Identifier: MIT
import { createApp } from 'vue'
import App from './App.vue'
import { locale } from './i18n'
import { createAppRouter } from './router'
import './styles.css'
import { applyTheme } from './theme'

document.documentElement.lang = locale.value
applyTheme()
createApp(App).use(createAppRouter()).mount('#app')
