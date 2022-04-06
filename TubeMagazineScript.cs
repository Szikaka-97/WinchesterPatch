using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Receiver2;
using UnityEngine;

namespace WinchesterPatch 
{
    class TubeMagazineScript : MonoBehaviour{

        public readonly int maxCapacity = 5;
        private Stack<ShellCasingScript> rounds = new Stack<ShellCasingScript>(5);
        private Transform[] roundPositions = new Transform[5];
        private Transform loadPosition;
        private Transform follower;
        private Vector3 followerStartPosition;

        private float speed_mul = 14.5f;

        public float loadProgress = 1;
        public InventorySlot slot;

        public int ammoCount {
            get { return rounds.Count; }
        }

        public bool ready {
            get { return loadProgress == 1; }
        }

        private void Awake() {
            slot = base.GetComponent<InventorySlot>();

            for (int i = 0; i < maxCapacity; i++) {
                roundPositions[i] = transform.Find("round_" + i.ToString());
            }

            if ( !roundPositions.Any(tr => tr == null) ) {
                Debug.Log("Successfully configured round positions for Winchester 1897");
            } else {
                Debug.LogError("Something went wrong while configuring round positions for Winchester 1897");
            }

            loadPosition = transform.Find("round_load");
            follower = transform.Find("follower");
            followerStartPosition = transform.Find("follower_start").localPosition;
        }

        private void updateRoundPositions() {
            for (int i = 0; i < rounds.Count; i++) {
                if (i == 0 && loadProgress != 1) continue;
                if (rounds.ElementAt(i).transform.localPosition.x != roundPositions[i].localPosition.x) {
                    rounds.ElementAt(i).transform.localPosition = new Vector3(
                        Mathf.MoveTowards(rounds.ElementAt(i).transform.localPosition.x, roundPositions[i].localPosition.x, Time.deltaTime * Time.timeScale), 
                        roundPositions[0].localPosition.y, 
                        roundPositions[0].localPosition.z
                    );
                }
            }

            if (loadProgress != 1) {
                rounds.ElementAt(0).transform.localPosition = Vector3.Lerp(loadPosition.localPosition, roundPositions[0].localPosition, loadProgress);
                rounds.ElementAt(0).transform.localRotation = Quaternion.Lerp(loadPosition.localRotation, roundPositions[0].localRotation, loadProgress);

                loadProgress = Mathf.MoveTowards(loadProgress, 1, Time.deltaTime * Time.timeScale * speed_mul);
            }
             
            follower.localPosition = new Vector3(
                Mathf.MoveTowards(follower.localPosition.x, -(5 - rounds.Count) * 0.068f, Time.deltaTime * Time.timeScale),
                0,
                0
            );
        }

        private void Update() {
            updateRoundPositions();
        }

        public void addRound(ShellCasingScript round) {
            if (round == null || rounds.Count >= maxCapacity) return;

            round.Move(slot);

            round.transform.parent = transform;
            round.transform.localScale = Vector3.one;
            round.transform.localPosition = loadPosition.localPosition;
            round.transform.localRotation = loadPosition.localRotation;

            rounds.Push(round);

            loadProgress = 0;

            AudioManager.PlayOneShotAttached("event:/guns/model10/insert_bullet", gameObject);
        }

        public ShellCasingScript removeRound() {
            if (rounds.Count > 0) return rounds.Pop();
            return null;
        }
    }
}
