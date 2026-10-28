<!-- SPDX-License-Identifier: MIT -->
<!-- Login with the admin password, or first-time password setup (route meta.setup). -->
<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, ApiError } from '../api/client'
import { errorText } from '../composables'
import { t } from '../i18n'
import { SETUP_SKIPPED_KEY } from '../router'
import { store } from '../store'

const route = useRoute()
const router = useRouter()
const setup = computed(() => route.meta.setup === true)

const password = ref('')
const confirmPassword = ref('')
const error = ref<string | null>(null)
const busy = ref(false)

function target(): string {
  const r = route.query.redirect
  return typeof r === 'string' && r.startsWith('/') && !r.startsWith('/login') && !r.startsWith('/setup') ? r : '/'
}

async function submit(): Promise<void> {
  error.value = null
  if (setup.value) {
    if (password.value.length < 8) return void (error.value = t('setup.tooShort'))
    if (password.value !== confirmPassword.value) return void (error.value = t('setup.mismatch'))
  }
  busy.value = true
  try {
    if (setup.value) {
      await api.setPassword(password.value)
      // Some service builds log in on setup, others need an explicit login; do it if needed.
      const s = await store.refreshSession()
      if (!s.authenticated) await api.login(password.value)
    } else {
      await api.login(password.value)
    }
    await store.refreshSession()
    await router.replace(target())
  } catch (e) {
    error.value = e instanceof ApiError && e.status === 401 ? t('login.wrong') : errorText(e)
  } finally {
    busy.value = false
  }
}

function skip(): void {
  try {
    sessionStorage.setItem(SETUP_SKIPPED_KEY, '1')
  } catch {
    // Not persisted: the setup page shows again on the next navigation.
  }
  void router.replace(target())
}
</script>

<template>
  <section class="login">
    <form class="card" novalidate @submit.prevent="submit">
      <h1>{{ setup ? t('setup.title') : t('login.title') }}</h1>
      <p v-if="setup" class="muted">{{ t('setup.hint') }}</p>
      <div class="field">
        <label for="pw">{{ setup ? t('setup.password') : t('login.password') }}</label>
        <input
          id="pw"
          v-model="password"
          type="password"
          name="password"
          :autocomplete="setup ? 'new-password' : 'current-password'"
          autofocus
        />
      </div>
      <div v-if="setup" class="field">
        <label for="pw2">{{ t('setup.confirm') }}</label>
        <input id="pw2" v-model="confirmPassword" type="password" name="confirm" autocomplete="new-password" />
      </div>
      <p v-if="error" class="error" role="alert">{{ error }}</p>
      <div class="row">
        <button v-if="setup" type="button" @click="skip">{{ t('setup.skip') }}</button>
        <div class="spacer" />
        <button type="submit" class="primary" :disabled="busy">{{ setup ? t('setup.submit') : t('login.submit') }}</button>
      </div>
    </form>
  </section>
</template>

<style scoped>
.login {
  display: grid;
  place-items: center;
  min-height: 70vh;
}

.login form {
  width: min(420px, 100%);
}
</style>
