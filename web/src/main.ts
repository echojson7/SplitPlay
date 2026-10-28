// SPDX-License-Identifier: MIT
import { createApp } from 'vue'
import App from './App.vue'
import { locale } from './i18n'
import { createAppRouter } from './router'
import './styles.css'

document.documentElement.lang = locale.value
createApp(App).use(createAppRouter()).mount('#app')
