<!-- SPDX-License-Identifier: MIT -->
<!-- Login with the admin password. Without one, only the tray (or the CLI) can manage the host. -->
<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, ApiError } from '../api/client'
import { errorText } from '../composables'
import { t } from '../i18n'
import { store } from '../store'
import BrandMark from '../components/BrandMark.vue'

const route = useRoute()
const router = useRouter()
const noPassword = computed(() => store.state.session?.setupRequired === true)

const password = ref('')
const error = ref<string | null>(null)
const busy = ref(false)

function target(): string {
  const r = route.query.redirect
  return typeof r === 'string' && r.startsWith('/') && !r.startsWith('/login') ? r : '/'
}

async function submit(): Promise<void> {
  error.value = null
  busy.value = true
  try {
    await api.login(password.value)
    await store.refreshSession()
    await router.replace(target())
  } catch (e) {
    error.value = e instanceof ApiError && e.status === 401 ? t('login.wrong') : errorText(e)
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <section class="login">
    <div class="brand">
      <BrandMark :size="20" />
      <span class="wordmark">SplitPlay</span>
    </div>
    <div v-if="noPassword" class="box" data-use-tray>
      <h1 class="title">{{ t('login.title') }}</h1>
      <p class="muted">{{ t('login.useTray') }}</p>
    </div>
    <form v-else class="box" novalidate @submit.prevent="submit">
      <h1 class="title">{{ t('login.title') }}</h1>
      <div class="field">
        <label for="pw">{{ t('login.password') }}</label>
        <input id="pw" v-model="password" type="password" name="password" autocomplete="current-password" autofocus />
      </div>
      <p v-if="error" class="err" role="alert">{{ error }}</p>
      <button type="submit" class="btn-primary submit" :disabled="busy">{{ t('login.submit') }}</button>
    </form>
  </section>
</template>

<style scoped>
.login {
  min-height: 100vh;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 20px;
  padding: 24px 16px;
}

.brand {
  display: flex;
  align-items: center;
  gap: 10px;
  color: var(--text);
}

.wordmark {
  font-size: 14px;
}

.box {
  width: min(360px, 100%);
  display: flex;
  flex-direction: column;
  gap: 16px;
  padding: 24px;
  border-radius: 8px;
  background: var(--row);
  border: 1px solid var(--border);
}

.title {
  font-size: 20px;
  line-height: 28px;
  font-weight: 600;
}

.muted {
  margin: 0;
}

.err {
  margin: 0;
}

.submit {
  margin-top: 4px;
}
</style>
