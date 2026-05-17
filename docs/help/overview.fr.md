---
title: Présentation d'Andy Containers
slug: andy-containers-overview
order: 1
tags: [containers, workspaces, runtime]
---

# Présentation d'Andy Containers

Andy Containers est le service d'orchestration de conteneurs de l'écosystème Andy. Il possède les workspaces, les modèles et le cycle de vie de chaque conteneur que Conductor lance — local (Docker/Apple Containers) ou distant (Rivoli AI Cloud).

## Ce qu'il fait

- Crée et détruit des workspaces à partir de modèles (image, ports, env, volumes, multiplexeur).
- Suit l'état des conteneurs et expose les événements de cycle de vie que l'UI Conductor consomme via SSE.
- Route les sessions d'attachement IDE et de terminal via une surface d'API Docker contrôlée qui applique RBAC par verbe.
- Gère les images de conteneur : pull, list, prune. Les pulls s'authentifient contre les registres frontés par `andy-mcp-proxy` lorsque configurés.
- Réconcilie les dérives — les conteneurs tués hors de Conductor sont détectés au prochain sondage et exposés comme arrêtés.

## Concepts clés

- **Workspace** — une unité orientée utilisateur : un conteneur, son modèle et les volumes auxquels il est attaché. Possède son propre cycle de vie indépendant du conteneur sous-jacent.
- **Modèle** — définition YAML ou publiée au registre ; la source de vérité pour ce qu'obtient un nouveau workspace.
- **Backend d'exécution** — `docker-passthrough` (Docker local), `apple-containers` (Apple Containers local), ou `cloud-tunnel` (Rivoli AI Cloud via WSS sortant). Même ensemble de verbes API Docker à travers les trois.

## Où il s'intègre

L'onglet Workspaces de Conductor parle à Containers pour chaque lecture et écriture. Les exécutions d'agents s'exécutent *dans* les conteneurs de workspace, donc Containers est sur le chemin critique de la démo. Dépend d'Auth, RBAC et Settings.

## Configuration

Les répertoires de modèles, le runtime par défaut et les identifiants de pull d'image résident sous les clés `andy.containers.*` dans `andy-settings`. Conductor expose la surface modifiable sous **Réglages → Runtime Defaults**.

## Dépannage

- **Workspace bloqué en `Provisioning`** — le pull d'image est lent ou a échoué. Vérifiez le log du conteneur ; si le pull est en cause, vérifiez la référence d'identifiants de registre dans `andy-settings`.
- **L'attachement IDE échoue avec « container not found »** — le conteneur est mort entre la liste et l'attachement. Re-sondez la liste de workspaces ; l'UI se rétablit automatiquement.
- **Backend Apple Containers désactivé** — la version macOS ne prend pas en charge le framework `container` (15+ requis) ou l'entitlement manque.
